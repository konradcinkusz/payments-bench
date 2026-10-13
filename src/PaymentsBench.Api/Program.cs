using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using PaymentsBench.Application.Payments;
using PaymentsBench.Application.Ports;
using PaymentsBench.Application.Reconciliation;
using PaymentsBench.Domain;
using PaymentsBench.Domain.Payments;
using PaymentsBench.Infrastructure;
using PaymentsBench.ServiceDefaults;

// Program.cs is the manifest: what this service is made of, in the order it starts.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddPaymentsInfrastructure(builder.Configuration);
builder.Services.AddSingleton(new ActivitySource("PaymentsBench"));
builder.Services.AddSingleton(new Meter("PaymentsBench"));

var app = builder.Build();
app.Services.LogStorageMode();
app.MapDefaultEndpoints();
app.MapOpenApi();

var meter = app.Services.GetRequiredService<Meter>();
var transitions = meter.CreateCounter<long>("payments.transitions", description: "Payment status transitions, tagged by status.");

var payments = app.MapGroup("/payments");

payments.MapPost("/", async (
    [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
    InitiatePaymentRequest request,
    InitiatePaymentHandler handler,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(idempotencyKey))
    {
        return Results.Problem("The Idempotency-Key header is required.", statusCode: StatusCodes.Status400BadRequest);
    }

    try
    {
        var payment = await handler.HandleAsync(
            new InitiatePayment(idempotencyKey, request.EndToEndId, request.Amount, request.Currency, request.DebtorAccount, request.CreditorAccount, request.Scheme),
            cancellationToken);
        transitions.Add(1, new KeyValuePair<string, object?>("status", payment.Status.ToString()));
        return Results.Created($"/payments/{payment.Id}", PaymentView.From(payment));
    }
    catch (IdempotencyConflictException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
    catch (DomainException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
    }
});

payments.MapGet("/{id:guid}", async (Guid id, IPaymentRepository repository, CancellationToken cancellationToken) =>
{
    var payment = await repository.GetAsync(id, cancellationToken);
    return payment is null ? Results.NotFound() : Results.Ok(PaymentView.From(payment));
});

payments.MapPost("/{id:guid}/submit", async (Guid id, SubmitPaymentHandler handler, CancellationToken cancellationToken) =>
{
    try
    {
        var payment = await handler.HandleAsync(new SubmitPayment(id), cancellationToken);
        transitions.Add(1, new KeyValuePair<string, object?>("status", payment.Status.ToString()));
        return Results.Ok(PaymentView.From(payment));
    }
    catch (PaymentNotFoundException)
    {
        return Results.NotFound();
    }
    catch (InvalidTransitionException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
});

payments.MapPost("/{id:guid}/resolve", async (Guid id, ResolveSubmittedPaymentHandler handler, CancellationToken cancellationToken) =>
{
    try
    {
        var outcome = await handler.HandleAsync(new ResolveSubmittedPayment(id), cancellationToken);
        return Results.Ok(new { outcome = outcome.ToString() });
    }
    catch (PaymentNotFoundException)
    {
        return Results.NotFound();
    }
});

payments.MapGet("/{id:guid}/ledger", async (Guid id, ILedger ledger, CancellationToken cancellationToken) =>
    Results.Ok(await ledger.EntriesForAsync(id, cancellationToken)));

app.MapPost("/partner/notifications", async (PartnerNotification notification, ApplyPartnerNotificationHandler handler, CancellationToken cancellationToken) =>
{
    try
    {
        var outcome = await handler.HandleAsync(notification, cancellationToken);
        transitions.Add(1, new KeyValuePair<string, object?>("status", notification.Kind.ToString()));
        return Results.Ok(new { outcome = outcome.ToString() });
    }
    catch (InvalidTransitionException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
});

app.MapPost("/reconciliation/runs/{date}", async (DateOnly date, ReconciliationService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.ReconcileAsync(date, cancellationToken)));

app.MapGet("/ledger/balances/{account}/{currency}", async (string account, string currency, ILedger ledger, CancellationToken cancellationToken) =>
    Results.Ok(new { account, currency, balance = await ledger.BalanceAsync(account, currency, cancellationToken) }));

await app.RunAsync();

internal sealed record InitiatePaymentRequest(string EndToEndId, decimal Amount, string Currency, string DebtorAccount, string CreditorAccount, string Scheme);

internal sealed record PaymentView(Guid Id, string EndToEndId, decimal Amount, string Currency, string Status, string? ReasonCode, string? PartnerReference, DateOnly? ValueDate, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static PaymentView From(Payment p) => new(p.Id, p.EndToEndId, p.Amount.Amount, p.Amount.Currency, p.Status.ToString(), p.ReasonCode, p.PartnerReference, p.ValueDate, p.CreatedAt, p.UpdatedAt);
}
