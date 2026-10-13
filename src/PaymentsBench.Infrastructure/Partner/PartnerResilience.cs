using Microsoft.Extensions.Configuration;
using Polly;
using Polly.Retry;

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
    /// Retry a lookup (<c>GET</c>) that threw or answered 5xx; never retry anything else. The method
    /// is read from the request on the resilience context, because an attempt that threw has no
    /// response to read it from.
    /// </summary>
    public static ValueTask<bool> ShouldRetry(RetryPredicateArguments<HttpResponseMessage> args)
    {
        var method = args.Context.GetRequestMessage()?.Method ?? args.Outcome.Result?.RequestMessage?.Method;
        return ValueTask.FromResult(
            method == HttpMethod.Get
            && (args.Outcome.Exception is not null || (int?)args.Outcome.Result?.StatusCode >= 500));
    }
}
