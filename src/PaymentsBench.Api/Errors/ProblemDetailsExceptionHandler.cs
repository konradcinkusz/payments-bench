using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using PaymentsBench.Application.Payments;
using PaymentsBench.Domain;
using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Api.Errors;

/// <summary>
/// The one place an exception becomes an HTTP status. Every error leaves the service as
/// <c>application/problem+json</c>. Domain errors carry messages written for callers; anything
/// else is logged here and answered with a fixed text, so no internal message leaks.
/// </summary>
public sealed partial class ProblemDetailsExceptionHandler(IProblemDetailsService problemDetails, ILogger<ProblemDetailsExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            PaymentNotFoundException e => (StatusCodes.Status404NotFound, "Payment not found", e.Message),
            IdempotencyConflictException e => (StatusCodes.Status409Conflict, "Idempotency conflict", e.Message),
            InvalidTransitionException e => (StatusCodes.Status409Conflict, "Invalid transition", e.Message),
            DomainException e => (StatusCodes.Status422UnprocessableEntity, "Request cannot be processed", e.Message),
            BadHttpRequestException e => (e.StatusCode, "Bad request", "The request could not be read."),
            _ => (StatusCodes.Status500InternalServerError, "Internal server error", "An unexpected error occurred."),
        };

        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogUnhandled(logger, exception);
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail },
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception.")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);
}
