using Microsoft.AspNetCore.Http.Features;
using PaymentsBench.Api.Contracts;
using PaymentsBench.Api.Errors;
using PaymentsBench.Application.Payments;
using PaymentsBench.Domain;
using PaymentsBench.Iso20022;

namespace PaymentsBench.Api.Endpoints;

/// <summary>
/// The ISO 20022 front door (P11): a pain.001 document is read into the internal commands here and
/// goes through the same <see cref="InitiatePaymentHandler"/> as the JSON body does.
/// </summary>
public static class Pain001Endpoints
{
    /// <summary>The largest document accepted, in bytes. Explicit, because an unbounded XML body is an invitation.</summary>
    public const int MaxBodyBytes = 1_048_576;

    public const string XmlMediaType = "application/xml";

    public static IEndpointRouteBuilder MapPain001Endpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/payments/pain001", InitiateAsync)
            .WithTags(PaymentsEndpoints.Tag)
            .WithName("InitiatePaymentsFromPain001")
            .Accepts<string>(XmlMediaType)
            .Produces<Pain001ResultView>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
        return app;
    }

    /// <summary>
    /// A document that cannot be read is a 400 naming the element. One that reads is answered 200 with a
    /// result per transaction: each is its own unit of work, so one refused transaction (unknown scheme,
    /// idempotency conflict) does not undo the others, and re-posting the file replays the rest.
    /// </summary>
    private static async Task<IResult> InitiateAsync(
        HttpRequest request,
        InitiatePaymentHandler handler,
        PaymentMetrics metrics,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.ContentType?.Split(';')[0].Trim(), XmlMediaType, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: "Unsupported media type",
                detail: $"Send the document as {XmlMediaType}.");
        }

        var sizeLimit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeLimit is { IsReadOnly: false })
        {
            sizeLimit.MaxRequestBodySize = MaxBodyBytes;
        }

        var commands = await Pain001Reader.ReadAsync(request.Body, MaxBodyBytes, cancellationToken);

        var results = new List<Pain001TransactionView>(commands.Count);
        foreach (var command in commands)
        {
            try
            {
                var payment = await handler.HandleAsync(command, cancellationToken);
                metrics.Transitioned(payment.Status.ToString());
                results.Add(new Pain001TransactionView(command.EndToEndId, payment.Id, payment.Status.ToString(), null));
            }
            catch (DomainException ex)
            {
                var (status, title, detail) = ProblemClassifier.Classify(ex);
                results.Add(new Pain001TransactionView(command.EndToEndId, null, null, new ProblemView(status, title, detail)));
            }
        }

        return Results.Ok(new Pain001ResultView(results));
    }
}
