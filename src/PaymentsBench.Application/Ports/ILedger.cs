using PaymentsBench.Domain.Ledger;

namespace PaymentsBench.Application.Ports;

public interface ILedger
{
    Task PostAsync(JournalEntry entry, CancellationToken cancellationToken);

    Task<IReadOnlyList<JournalEntry>> EntriesForAsync(Guid paymentId, CancellationToken cancellationToken);

    /// <summary>Debits minus credits for an account in a currency, derived from entries, never stored.</summary>
    Task<decimal> BalanceAsync(string account, string currency, CancellationToken cancellationToken);
}
