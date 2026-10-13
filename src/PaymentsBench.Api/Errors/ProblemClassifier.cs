using PaymentsBench.Application.Payments;
using PaymentsBench.Domain;
using PaymentsBench.Domain.Payments;
using PaymentsBench.Iso20022;

namespace PaymentsBench.Api.Errors;

/// <summary>
/// Which status, title and detail an exception means. The exception handler answers a whole request
/// with it; the pain.001 endpoint answers one transaction of a batch with it. One table, two callers.
/// </summary>
public static class ProblemClassifier
{
    public static (int Status, string Title, string Detail) Classify(Exception exception) => exception switch
    {
        PaymentNotFoundException e => (StatusCodes.Status404NotFound, "Payment not found", e.Message),
        IdempotencyConflictException e => (StatusCodes.Status409Conflict, "Idempotency conflict", e.Message),
        InvalidTransitionException e => (StatusCodes.Status409Conflict, "Invalid transition", e.Message),
        DomainException e => (StatusCodes.Status422UnprocessableEntity, "Request cannot be processed", e.Message),
        MessageTooLargeException e => (StatusCodes.Status413PayloadTooLarge, "Document too large", e.Message),
        MessageFormatException e => (StatusCodes.Status400BadRequest, "Invalid ISO 20022 document", e.Message),
        BadHttpRequestException e => (e.StatusCode, "Bad request", "The request could not be read."),
        _ => (StatusCodes.Status500InternalServerError, "Internal server error", "An unexpected error occurred."),
    };
}
