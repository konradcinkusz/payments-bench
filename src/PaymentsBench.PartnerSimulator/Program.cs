using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using PaymentsBench.PartnerSimulator;
using PaymentsBench.ServiceDefaults;

// A bank that misbehaves on request. Every failure mode a payment rail meets in production is a
// fault profile here, switched at runtime, so a scenario can say "the partner times out after
// accepting" and the API under test has to cope.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
app.MapDefaultEndpoints();

var calls = new CallCounter();
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/diagnostics", StringComparison.Ordinal))
    {
        calls.Record(context.Request.Method, context.GetEndpoint() is RouteEndpoint route ? route.RoutePattern.RawText ?? string.Empty : context.Request.Path.Value ?? string.Empty);
    }

    await next(context);
});

var transfers = new ConcurrentDictionary<string, SimulatedTransfer>(StringComparer.Ordinal);
var profile = new FaultProfileState();

// SIMULATED diagnostics: how many times the simulated partner was called, per "METHOD route-pattern",
// counted when the request arrives. It is the partner's own word on what reached it, which the
// scenario runner uses to assert "submitted once" without trusting the API's account of itself.
app.MapGet("/diagnostics/calls", () => Results.Ok(new { simulated = true, calls = calls.Snapshot() }));

app.MapGet("/faults", () => Results.Ok(profile.Current));

app.MapPut("/faults", (FaultProfile requested) =>
{
    profile.Current = requested;
    return Results.Ok(profile.Current);
});

app.MapPost("/transfers", async (TransferRequest request, CancellationToken cancellationToken) =>
{
    var current = profile.Current;
    if (current.Kind == FaultKind.ServerError)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    if (current.Kind == FaultKind.RejectAll)
    {
        return Results.UnprocessableEntity(new { reasonCode = current.ReasonCode ?? "AC01" });
    }

    var transfer = transfers.GetOrAdd(request.EndToEndId, id => new SimulatedTransfer(id, $"PTR-{Guid.NewGuid():N}"[..12], request.Amount, request.Currency, "Accepted", null, null));

    if (current.Kind == FaultKind.TimeoutAfterAccept)
    {
        // The transfer exists on our side; the caller never hears about it. The resolver has to ask.
        await Task.Delay(current.DelayMilliseconds ?? 10_000, cancellationToken);
    }
    else if (current.Kind == FaultKind.Latency)
    {
        await Task.Delay(current.DelayMilliseconds ?? 1_500, cancellationToken);
    }

    return Results.Ok(new { partnerReference = transfer.PartnerReference });
});

app.MapGet("/transfers/{endToEndId}", (string endToEndId) =>
    transfers.TryGetValue(endToEndId, out var transfer)
        ? Results.Ok(new { status = transfer.Status, partnerReference = transfer.PartnerReference, reasonCode = transfer.ReasonCode, valueDate = transfer.ValueDate })
        : Results.NotFound());

app.MapPost("/transfers/{endToEndId}/settle", (string endToEndId, DateOnly? valueDate) =>
{
    if (!transfers.TryGetValue(endToEndId, out var transfer))
    {
        return Results.NotFound();
    }

    transfers[endToEndId] = transfer with { Status = "Settled", ValueDate = valueDate ?? DateOnly.FromDateTime(DateTime.UtcNow) };
    return Results.Ok(transfers[endToEndId]);
});

app.MapPost("/transfers/{endToEndId}/return", (string endToEndId, string? reasonCode) =>
{
    if (!transfers.TryGetValue(endToEndId, out var transfer))
    {
        return Results.NotFound();
    }

    transfers[endToEndId] = transfer with { Status = "Returned", ReasonCode = reasonCode ?? "AC04" };
    return Results.Ok(transfers[endToEndId]);
});

app.MapGet("/statements/{date}", (DateOnly date, HttpRequest request) =>
{
    var drift = profile.Current.Kind == FaultKind.StatementDrift ? profile.Current.DriftAmount ?? 0.01m : 0m;
    var lines = transfers.Values
        .Where(t => t.Status is "Settled" or "Returned" && t.ValueDate == date)
        .Select(t => new
        {
            endToEndId = t.EndToEndId,
            amount = t.Amount + drift,
            currency = t.Currency,
            bookingDate = date,
            direction = "Debit",
        })
        .ToList();

    // Content negotiation: a caller that accepts application/xml gets the camt.053 rendering; anyone else, the JSON reduction.
    if (request.GetTypedHeaders().Accept.Any(a => a.MediaType.Equals(StatementXml.MediaType, StringComparison.OrdinalIgnoreCase)))
    {
        return Results.Content(StatementXml.Render(date, "NOSTRO-SIM", lines.Select(l => (l.endToEndId, l.amount, l.currency, l.direction))), StatementXml.MediaType);
    }

    return Results.Ok(new { date, account = "NOSTRO-SIM", lines });
});

await app.RunAsync();

internal sealed class CallCounter
{
    private readonly ConcurrentDictionary<string, int> counts = new(StringComparer.Ordinal);

    public void Record(string method, string route) => counts.AddOrUpdate($"{method} {route}", 1, (_, n) => n + 1);

    public IReadOnlyDictionary<string, int> Snapshot() => new Dictionary<string, int>(counts, StringComparer.Ordinal);
}

internal enum FaultKind
{
    None = 0,
    Latency = 1,
    TimeoutAfterAccept = 2,
    ServerError = 3,
    RejectAll = 4,
    StatementDrift = 5,
}

internal sealed record FaultProfile(FaultKind Kind, int? DelayMilliseconds = null, string? ReasonCode = null, decimal? DriftAmount = null);

internal sealed class FaultProfileState
{
    public FaultProfile Current { get; set; } = new(FaultKind.None);
}

internal sealed record TransferRequest(string EndToEndId, decimal Amount, string Currency, string DebtorAccount, string CreditorAccount, string Scheme);

internal sealed record SimulatedTransfer(string EndToEndId, string PartnerReference, decimal Amount, string Currency, string Status, string? ReasonCode, DateOnly? ValueDate);
