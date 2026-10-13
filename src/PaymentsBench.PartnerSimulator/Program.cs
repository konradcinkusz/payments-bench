using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using PaymentsBench.ServiceDefaults;

// A bank that misbehaves on request. Every failure mode a payment rail meets in production is a
// fault profile here, switched at runtime, so a scenario can say "the partner times out after
// accepting" and the API under test has to cope.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
app.MapDefaultEndpoints();

var transfers = new ConcurrentDictionary<string, SimulatedTransfer>(StringComparer.Ordinal);
var profile = new FaultProfileState();

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

app.MapGet("/statements/{date}", (DateOnly date) =>
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
    return Results.Ok(new { date, account = "NOSTRO-SIM", lines });
});

await app.RunAsync();

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
