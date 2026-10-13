using Microsoft.Extensions.Configuration;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace PaymentsBench.Infrastructure.Partner;

/// <summary>
/// The timeouts of the partner client, read from <c>Partner:Timeout</c>, <c>Partner:AttemptTimeout</c>
/// and <c>Partner:TotalTimeout</c> (any <see cref="TimeSpan"/> text, for example <c>00:00:01</c>).
/// Defaults are the values the service always had; a scenario run shortens them (P5).
/// </summary>
public sealed record PartnerTimeouts(TimeSpan Client, TimeSpan Attempt, TimeSpan Total)
{
    public static readonly PartnerTimeouts Defaults = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(15));

    public static PartnerTimeouts From(IConfiguration configuration) => new(
        configuration.GetSection("Partner").GetValue<TimeSpan?>("Timeout") ?? Defaults.Client,
        configuration.GetSection("Partner").GetValue<TimeSpan?>("AttemptTimeout") ?? Defaults.Attempt,
        configuration.GetSection("Partner").GetValue<TimeSpan?>("TotalTimeout") ?? Defaults.Total);
}

public static class PartnerResilience
{
    /// <summary>
    /// Retry a lookup (<c>GET</c>) that threw a transport error, timed out per attempt or answered 5xx;
    /// never retry anything else. The method is read from the request on the resilience context, because
    /// an attempt that threw has no response to read it from.
    /// <para>
    /// An open circuit is not retried: <see cref="BrokenCircuitException"/> (and its subclass
    /// <see cref="IsolatedCircuitException"/>) means the breaker is shedding load, so another attempt
    /// would only wait out the backoff and be rejected again, holding a request thread during the very
    /// outage the breaker exists to absorb (P13; SERVICE-API-PATTERNS §5). Likewise a timeout that
    /// comes from the total-request budget: the context token is already cancelled, nothing is left to
    /// spend. A per-attempt timeout leaves the context token alone and stays retryable.
    /// </para>
    /// </summary>
    public static ValueTask<bool> ShouldRetry(RetryPredicateArguments<HttpResponseMessage> args)
    {
        var method = args.Context.GetRequestMessage()?.Method ?? args.Outcome.Result?.RequestMessage?.Method;
        if (method != HttpMethod.Get || args.Context.CancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(false);
        }

        return ValueTask.FromResult(args.Outcome.Exception switch
        {
            BrokenCircuitException => false,
            TimeoutRejectedException => true,
            not null => true,
            null => (int?)args.Outcome.Result?.StatusCode >= 500,
        });
    }
}
