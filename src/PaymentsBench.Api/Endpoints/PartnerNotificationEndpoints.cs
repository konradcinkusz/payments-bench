using PaymentsBench.Api.Contracts;
using PaymentsBench.Application.Payments;

namespace PaymentsBench.Api.Endpoints;

public static class PartnerNotificationEndpoints
{
    public static IEndpointRouteBuilder MapPartnerNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/partner/notifications", ApplyAsync)
            .WithTags("Partner notifications")
            .WithName("ApplyPartnerNotification")
            .Produces<OutcomeView>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return app;
    }

    private static async Task<IResult> ApplyAsync(PartnerNotificationRequest request, ApplyPartnerNotificationHandler handler, PaymentMetrics metrics, CancellationToken cancellationToken)
    {
        var notification = request.ToNotification();
        var outcome = await handler.HandleAsync(notification, cancellationToken);
        metrics.Transitioned(notification.Kind.ToString());
        return Results.Ok(new OutcomeView(outcome.ToString()));
    }
}
