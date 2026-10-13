namespace PaymentsBench.Domain.Payments;

/// <summary>
/// The one place the state machine is written down. Tests enumerate this table; the aggregate
/// consults it before every move.
/// </summary>
public static class PaymentTransitions
{
    private static readonly Dictionary<PaymentStatus, IReadOnlySet<PaymentStatus>> Allowed =
        new Dictionary<PaymentStatus, IReadOnlySet<PaymentStatus>>
        {
            [PaymentStatus.Initiated] = new HashSet<PaymentStatus> { PaymentStatus.Submitted },
            [PaymentStatus.Submitted] = new HashSet<PaymentStatus> { PaymentStatus.Accepted, PaymentStatus.Rejected, PaymentStatus.Settled },
            [PaymentStatus.Accepted] = new HashSet<PaymentStatus> { PaymentStatus.Settled, PaymentStatus.Rejected },
            [PaymentStatus.Settled] = new HashSet<PaymentStatus> { PaymentStatus.Returned },
            [PaymentStatus.Rejected] = new HashSet<PaymentStatus>(),
            [PaymentStatus.Returned] = new HashSet<PaymentStatus>(),
        };

    public static bool CanMove(PaymentStatus from, PaymentStatus to) => Allowed[from].Contains(to);

    public static bool IsTerminal(PaymentStatus status) => Allowed[status].Count == 0;

    public static IEnumerable<(PaymentStatus From, PaymentStatus To)> All() =>
        Allowed.SelectMany(pair => pair.Value.Select(to => (pair.Key, to)));
}
