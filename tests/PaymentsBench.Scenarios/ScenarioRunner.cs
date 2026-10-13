using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaymentsBench.Scenarios;

/// <summary>
/// Executes one scenario against a fresh API and simulated partner. Each step is real HTTP on one
/// of the two services; each <c>expect</c> key is asserted against what those services say, the
/// partner's counts coming from the simulator, which is the partner's own word.
/// </summary>
public sealed class ScenarioRunner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ScenarioDefinition scenario;
    private readonly StorageKind storage;
    private readonly Dictionary<string, Guid> ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> schemes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateOnly> partnerSettledOn = new(StringComparer.Ordinal);
    private ScenarioHost host = null!;
    private string? firstEndToEndId;
    private string? currency;
    private JsonElement? lastReport;
    private ScenarioStep? current;

    private ScenarioRunner(ScenarioDefinition scenario, StorageKind storage)
    {
        this.scenario = scenario;
        this.storage = storage;
    }

    public static async Task RunAsync(ScenarioDefinition scenario, StorageKind storage)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var runner = new ScenarioRunner(scenario, storage);
        await using var host = await ScenarioHost.StartAsync(storage, scenario.Clock);
        runner.host = host;
        await runner.ExecuteAsync();
    }

    private string Where => current is null
        ? $"Scenario '{scenario.Id}' ({scenario.FileName}, {storage})"
        : $"Scenario '{scenario.Id}' ({scenario.FileName}, {storage}), step {current.Number} '{current.Name}'";

    private async Task ExecuteAsync()
    {
        if (scenario.Partner is { } initial)
        {
            current = new ScenarioStep(0, "partner (top level)", null, new Dictionary<string, string>());
            await SetFaultAsync(initial);
        }

        foreach (var step in scenario.Steps)
        {
            current = step;
            await RunStepAsync(step);
        }

        current = null;
        await AssertExpectationsAsync(scenario.Expect);
    }

    private async Task RunStepAsync(ScenarioStep step)
    {
        switch (step.Name)
        {
            case "initiate":
                await InitiateAsync(step, expectConflict: false);
                break;
            case "initiateExpectingConflict":
                await InitiateAsync(step, expectConflict: true);
                break;
            case "submit":
                await PostAsync(host.Api, $"/payments/{IdOf(step.Scalar!)}/submit");
                break;
            case "assertStatus":
                Equal("payment status", step.Scalar!, (await PaymentAsync()).GetProperty("status").GetString());
                break;
            case "partnerFault":
                await SetFaultAsync(step.Scalar is { } name ? new PartnerFaultSetting(name, null, null, null) : new PartnerFaultSetting(step.Required("fault"), Decimal(step.Optional("driftAmount")), step.Optional("reasonCode"), Int(step.Optional("delayMilliseconds"))));
                break;
            case "resolve":
                await PostAsync(host.Api, $"/payments/{IdOf(step.Scalar!)}/resolve");
                break;
            case "partnerSettles":
                await PartnerSettlesAsync(step);
                break;
            case "partnerReturns":
                await PostAsync(host.Partner, $"/transfers/{Uri.EscapeDataString(step.Required("endToEndId"))}/return{(step.Optional("reasonCode") is { } reason ? $"?reasonCode={Uri.EscapeDataString(reason)}" : string.Empty)}");
                break;
            case "notify":
                await NotifyAsync(step);
                break;
            case "reconcile":
                await ReconcileAsync(step);
                break;
            default:
                throw new ScenarioFailedException($"{Where}: unknown step '{step.Name}'.");
        }
    }

    private async Task InitiateAsync(ScenarioStep step, bool expectConflict)
    {
        var endToEndId = step.Required("endToEndId");
        var body = new
        {
            endToEndId,
            amount = decimal.Parse(step.Required("amount"), CultureInfo.InvariantCulture),
            currency = step.Required("currency"),
            debtorAccount = step.Optional("debtorAccount") ?? "DEBTOR-ACCOUNT-1",
            creditorAccount = step.Optional("creditorAccount") ?? "CREDITOR-ACCOUNT-1",
            scheme = step.Required("scheme"),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/payments") { Content = JsonContent.Create(body, options: Json) };
        request.Headers.Add("Idempotency-Key", step.Required("idempotencyKey"));
        using var response = await host.Api.SendAsync(request);

        if (expectConflict)
        {
            Equal("HTTP status", (int)HttpStatusCode.Conflict, (int)response.StatusCode, await response.Content.ReadAsStringAsync());
            return;
        }

        var payment = await ReadAsync(response);
        ids[endToEndId] = payment.GetProperty("id").GetGuid();
        schemes[endToEndId] = body.scheme;
        firstEndToEndId ??= endToEndId;
        currency ??= body.currency;
    }

    private async Task SetFaultAsync(PartnerFaultSetting setting)
    {
        var kind = string.Concat(setting.Fault.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        var delay = setting.DelayMilliseconds ?? (kind == "TimeoutAfterAccept" ? ScenarioHost.DefaultTimeoutAfterAcceptMilliseconds : null);
        using var response = await host.Partner.PutAsJsonAsync("/faults", new { kind, delayMilliseconds = delay, reasonCode = setting.ReasonCode, driftAmount = setting.DriftAmount }, Json);
        await ReadAsync(response, $"set partner fault '{setting.Fault}'");
    }

    private async Task PartnerSettlesAsync(ScenarioStep step)
    {
        var endToEndId = step.Required("endToEndId");
        var valueDate = ResolveDate(step.Required("valueDate"), endToEndId);
        await PostAsync(host.Partner, $"/transfers/{Uri.EscapeDataString(endToEndId)}/settle?valueDate={valueDate:yyyy-MM-dd}");
        partnerSettledOn[endToEndId] = valueDate;
    }

    private async Task NotifyAsync(ScenarioStep step)
    {
        var endToEndId = step.Required("endToEndId");
        var kind = step.Required("kind");
        DateOnly? valueDate = step.Optional("valueDate") is { } given
            ? ResolveDate(given, endToEndId)
            : kind == "Settled" && partnerSettledOn.TryGetValue(endToEndId, out var settled) ? settled : null;
        await PostAsync(host.Api, "/partner/notifications", new { notificationId = step.Required("notificationId"), endToEndId, kind, reasonCode = step.Optional("reasonCode"), valueDate });
    }

    private async Task ReconcileAsync(ScenarioStep step)
    {
        DateOnly date;
        if (step.Scalar == "value-date")
        {
            var payment = await PaymentAsync();
            date = payment.GetProperty("valueDate").ValueKind == JsonValueKind.String
                ? DateOnly.Parse(payment.GetProperty("valueDate").GetString()!, CultureInfo.InvariantCulture)
                : throw new ScenarioFailedException($"{Where}: the payment has no value date to reconcile.");
        }
        else
        {
            date = DateOnly.ParseExact(step.Scalar!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        lastReport = await PostAsync(host.Api, $"/reconciliation/runs/{date:yyyy-MM-dd}");
    }

    /// <summary><c>next-business-day</c> is the first business day after the scenario clock on the payment's scheme calendar, from the service's own catalog.</summary>
    private DateOnly ResolveDate(string text, string endToEndId)
    {
        if (text != "next-business-day")
        {
            return DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var scheme = host.Schemes.Find(schemes.GetValueOrDefault(endToEndId, string.Empty))
            ?? throw new ScenarioFailedException($"{Where}: no scheme known for '{endToEndId}' to resolve next-business-day on.");
        var day = DateOnly.FromDateTime(scenario.Clock.UtcDateTime);
        do
        {
            day = day.AddDays(1);
        }
        while (!scheme.Calendar.IsBusinessDay(day));

        return day;
    }

    private async Task AssertExpectationsAsync(Expectations expect)
    {
        if (expect.Status is not null)
        {
            Equal("expect.status", expect.Status, (await PaymentAsync()).GetProperty("status").GetString());
        }

        if (expect.ValueDate is { } valueDate)
        {
            Equal("expect.valueDate", valueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), (await PaymentAsync()).GetProperty("valueDate").GetString());
        }

        if (expect.Payments is { } payments)
        {
            Equal("expect.payments", payments, (await GetAsync(host.Api, "/payments?limit=1")).GetProperty("total").GetInt32());
        }

        if (expect.LedgerEntries is { } entries)
        {
            Equal("expect.ledgerEntries", entries, (await GetAsync(host.Api, $"/payments/{FirstId()}/ledger")).GetArrayLength());
        }

        foreach (var (account, balance) in expect.Ledger ?? new Dictionary<string, decimal>())
        {
            var actual = (await GetAsync(host.Api, $"/ledger/balances/{Uri.EscapeDataString(account)}/{Uri.EscapeDataString(currency ?? "EUR")}")).GetProperty("balance").GetDecimal();
            Equal($"expect.ledger.{account}", balance, actual);
        }

        if (expect.PartnerSubmits is not null || expect.PartnerStatusQueries is not null)
        {
            var calls = (await GetAsync(host.Partner, "/diagnostics/calls")).GetProperty("calls");
            if (expect.PartnerSubmits is { } submits)
            {
                Equal("expect.partnerSubmits (counted at the simulated partner)", submits, Count(calls, "POST /transfers"));
            }

            if (expect.PartnerStatusQueries is { } queries)
            {
                Equal("expect.partnerStatusQueries (counted at the simulated partner)", queries, Count(calls, "GET /transfers/{endToEndId}"));
            }
        }

        if (expect.Reconciliation is { } reconciliation)
        {
            var report = lastReport ?? throw new ScenarioFailedException($"{Where}: expect.reconciliation is set but the scenario never ran a 'reconcile' step.");
            var breaks = report.GetProperty("breaks");
            if (reconciliation.Breaks is { } count)
            {
                Equal("expect.reconciliation.breaks", count, breaks.GetArrayLength());
            }

            if (reconciliation.Kinds is { } kinds)
            {
                var actual = breaks.EnumerateArray().Select(b => b.GetProperty("kind").GetString()!).Order(StringComparer.Ordinal).ToList();
                Equal("expect.reconciliation.kinds", $"[{string.Join(", ", kinds.Order(StringComparer.Ordinal))}]", $"[{string.Join(", ", actual)}]");
            }
        }
    }

    private static int Count(JsonElement calls, string key) => calls.TryGetProperty(key, out var count) ? count.GetInt32() : 0;

    private Guid IdOf(string endToEndId) =>
        ids.TryGetValue(endToEndId, out var id) ? id : throw new ScenarioFailedException($"{Where}: '{endToEndId}' was never initiated by this scenario.");

    private Guid FirstId() =>
        firstEndToEndId is null ? throw new ScenarioFailedException($"{Where}: the scenario initiated no payment.") : ids[firstEndToEndId];

    private Task<JsonElement> PaymentAsync() => GetAsync(host.Api, $"/payments/{FirstId()}");

    private async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        return await ReadAsync(response);
    }

    private async Task<JsonElement> PostAsync(HttpClient client, string path, object? body = null)
    {
        using var response = body is null ? await client.PostAsync(path, content: null) : await client.PostAsJsonAsync(path, body, Json);
        return await ReadAsync(response);
    }

    private async Task<JsonElement> ReadAsync(HttpResponseMessage response, string? what = null)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new ScenarioFailedException($"{Where}: {what ?? $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}"} expected a success status but was {(int)response.StatusCode}: {text}");
        }

        using var document = JsonDocument.Parse(text.Length == 0 ? "null" : text);
        return document.RootElement.Clone();
    }

    private void Equal<T>(string what, T expected, T actual, string? detail = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new ScenarioFailedException($"{Where}: {what} expected {expected} but was {actual}.{(detail is null ? string.Empty : " " + detail)}");
        }
    }

    private static decimal? Decimal(string? text) => text is null ? null : decimal.Parse(text, CultureInfo.InvariantCulture);

    private static int? Int(string? text) => text is null ? null : int.Parse(text, CultureInfo.InvariantCulture);
}
