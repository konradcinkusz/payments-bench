namespace PaymentsBench.Domain.Ledger;

/// <summary>
/// The chart of accounts, as names rather than an enum so a new account is data, not a release.
/// Customer funds are safeguarded: they sit in <see cref="CustomerFunds"/> until a transfer is
/// initiated, in <see cref="PendingOutbound"/> while it is in flight, and leave through
/// <see cref="Nostro"/> only when the partner says it settled.
/// </summary>
public static class LedgerAccounts
{
    public const string CustomerFunds = "customer-funds";
    public const string PendingOutbound = "pending-outbound";
    public const string Nostro = "nostro";
}
