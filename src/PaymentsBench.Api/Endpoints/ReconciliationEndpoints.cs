using PaymentsBench.Application.Reconciliation;

namespace PaymentsBench.Api.Endpoints;

public static class ReconciliationEndpoints
{
    public static IEndpointRouteBuilder MapReconciliationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/reconciliation/runs/{date}", RunAsync)
            .WithTags("Reconciliation")
            .WithName("RunReconciliation")
            .Produces<ReconciliationReport>()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return app;
    }

    private static async Task<IResult> RunAsync(DateOnly date, ReconciliationService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.ReconcileAsync(date, cancellationToken));
}
