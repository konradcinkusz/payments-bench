using PaymentsBench.Api.Contracts;
using PaymentsBench.Application.Payments;
using PaymentsBench.Application.Ports;
using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Api.Endpoints;

public static class PaymentsEndpoints
{
    public const string Tag = "Payments";

    public static IEndpointRouteBuilder MapPaymentsEndpoints(this IEndpointRouteBuilder app)
    {
        var payments = app.MapGroup("/payments").WithTags(Tag);

        payments.MapPost("/", InitiateAsync)
            .WithName("InitiatePayment")
            .Produces<PaymentView>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        payments.MapGet("/", ListAsync)
            .WithName("ListPayments")
            .Produces<PaymentListView>()
            .ProducesValidationProblem();

        payments.MapGet("/{id:guid}", GetAsync)
            .WithName("GetPayment")
            .Produces<PaymentView>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        payments.MapPost("/{id:guid}/submit", SubmitAsync)
            .WithName("SubmitPayment")
            .Produces<PaymentView>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        payments.MapPost("/{id:guid}/resolve", ResolveAsync)
            .WithName("ResolvePayment")
            .Produces<OutcomeView>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> InitiateAsync(
        [AsParameters] IdempotencyHeader header,
        InitiatePaymentRequest request,
        InitiatePaymentHandler handler,
        PaymentMetrics metrics,
        CancellationToken cancellationToken)
    {
        var payment = await handler.HandleAsync(
            new InitiatePayment(header.IdempotencyKey!, request.EndToEndId, request.Amount, request.Currency, request.DebtorAccount, request.CreditorAccount, request.Scheme),
            cancellationToken);
        metrics.Transitioned(payment.Status.ToString());
        return Results.Created($"/payments/{payment.Id}", PaymentView.From(payment));
    }

    private static async Task<IResult> ListAsync([AsParameters] ListPaymentsRequest request, IPaymentRepository repository, CancellationToken cancellationToken)
    {
        var query = PaymentQuery.Create(
            request.Status is null ? null : Enum.Parse<PaymentStatus>(request.Status, ignoreCase: true),
            request.Scheme,
            request.EndToEndId,
            request.CreatedFrom,
            request.CreatedTo,
            request.Page,
            request.Limit);
        return Results.Ok(PaymentListView.From(query, await repository.ListAsync(query, cancellationToken)));
    }

    private static async Task<IResult> GetAsync(Guid id, IPaymentRepository repository, CancellationToken cancellationToken) =>
        Results.Ok(PaymentView.From(await repository.GetAsync(id, cancellationToken) ?? throw new PaymentNotFoundException(id)));

    private static async Task<IResult> SubmitAsync(Guid id, SubmitPaymentHandler handler, PaymentMetrics metrics, CancellationToken cancellationToken)
    {
        var payment = await handler.HandleAsync(new SubmitPayment(id), cancellationToken);
        metrics.Transitioned(payment.Status.ToString());
        return Results.Ok(PaymentView.From(payment));
    }

    private static async Task<IResult> ResolveAsync(Guid id, ResolveSubmittedPaymentHandler handler, CancellationToken cancellationToken)
    {
        var outcome = await handler.HandleAsync(new ResolveSubmittedPayment(id), cancellationToken);
        return Results.Ok(new OutcomeView(outcome.ToString()));
    }
}
