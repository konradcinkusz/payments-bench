using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using PaymentsBench.Iso20022;

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
        var (status, title, detail) = ProblemClassifier.Classify(exception);

        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogUnhandled(logger, exception);
        }

        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        if (exception is MessageFormatException { ElementPath.Length: > 0 } format)
        {
            problem.Extensions["element"] = format.ElementPath;
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problem,
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception.")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);
}
