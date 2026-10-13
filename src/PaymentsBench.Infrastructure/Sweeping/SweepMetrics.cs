using System.Diagnostics;
using System.Diagnostics.Metrics;
using PaymentsBench.Application.Payments;
using PaymentsBench.Application.Ports;

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
    private readonly IClock _clock;
    private long _oldestUpdatedAtTicks; // UTC ticks; 0 means no Submitted payment

    public SweepMetrics(IMeterFactory meterFactory, IClock clock)
    {
        _clock = clock;
        var meter = meterFactory.Create(MeterName);
        _resolved = meter.CreateCounter<long>(ResolvedName, unit: "{payment}", description: "Payments handled by a sweep, tagged by resolution outcome or 'failed'.");
        meter.CreateObservableGauge(OldestAgeName, ObserveOldestAgeSeconds, unit: "s", description: "Age of the oldest payment still at Submitted as of the last sweep; 0 when none.");
    }

    // The oldest payment is found by a sweep, but its age is read live: a sweeper that stops keeps the gauge rising while that payment waits.
    private double ObserveOldestAgeSeconds() =>
        Volatile.Read(ref _oldestUpdatedAtTicks) is var ticks and > 0 ? Math.Max(0, (_clock.UtcNow.UtcTicks - ticks) / (double)TimeSpan.TicksPerSecond) : 0;

    public void Record(SweepResult result)
    {
        Volatile.Write(ref _oldestUpdatedAtTicks, result.OldestSubmittedUpdatedAt?.UtcTicks ?? 0);
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
