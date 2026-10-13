using System.Diagnostics;
using System.Diagnostics.Metrics;
using PaymentsBench.Application.Payments;

namespace PaymentsBench.Infrastructure.Sweeping;

/// <summary>
/// The sweep's instruments on the shared <c>PaymentsBench</c> meter. The names are a contract with
/// the dashboard; ADR 0009 documents the alert on the gauge. The meter comes from
/// <see cref="IMeterFactory"/>, which hands every caller the same instance for a name.
/// </summary>
public sealed class SweepMetrics
{
    public const string MeterName = "PaymentsBench";
    public const string OldestAgeName = "payments.submitted.oldest_age_seconds";
    public const string ResolvedName = "payments.sweep.resolved";
    public const string FailedOutcome = "failed";

    private readonly Counter<long> _resolved;
    private double _oldestAgeSeconds;

    public SweepMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _resolved = meter.CreateCounter<long>(ResolvedName, unit: "{payment}", description: "Payments handled by a sweep, tagged by resolution outcome or 'failed'.");
        meter.CreateObservableGauge(OldestAgeName, () => Volatile.Read(ref _oldestAgeSeconds), unit: "s", description: "Age of the oldest payment still at Submitted as of the last sweep; 0 when none.");
    }

    public void Record(SweepResult result)
    {
        Volatile.Write(ref _oldestAgeSeconds, result.OldestSubmittedAge.TotalSeconds);
        foreach (var (outcome, count) in result.Outcomes)
        {
            _resolved.Add(count, new KeyValuePair<string, object?>("outcome", outcome.ToString()));
        }

        if (result.Failures.Count > 0)
        {
            _resolved.Add(result.Failures.Count, new KeyValuePair<string, object?>("outcome", FailedOutcome));
        }
    }
}
