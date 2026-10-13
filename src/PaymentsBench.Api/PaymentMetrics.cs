using System.Diagnostics.Metrics;

namespace PaymentsBench.Api;

/// <summary>The business counters the endpoints move, on the <c>PaymentsBench</c> meter.</summary>
public sealed class PaymentMetrics(Meter meter)
{
    private readonly Counter<long> _transitions = meter.CreateCounter<long>("payments.transitions", description: "Payment status transitions, tagged by status.");

    public void Transitioned(string status) => _transitions.Add(1, new KeyValuePair<string, object?>("status", status));
}
