using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Domain.Ledger;

/// <summary>
/// The journal entries each payment event produces. Kept together so the full money path of a
/// transfer can be read top to bottom: reserve, settle, and either release on rejection or
/// reverse on return.
/// </summary>
public static class Postings
{
    public static JournalEntry Reserve(Payment payment, DateTimeOffset now) => new(
        Guid.NewGuid(),
        payment.Id,
        $"Reserve {payment.Amount} for {payment.EndToEndId}",
        now,
        [
            new JournalLine(LedgerAccounts.CustomerFunds, Side.Debit, payment.Amount),
            new JournalLine(LedgerAccounts.PendingOutbound, Side.Credit, payment.Amount),
        ]);

    public static JournalEntry Settle(Payment payment, DateTimeOffset now) => new(
        Guid.NewGuid(),
        payment.Id,
        $"Settle {payment.Amount} for {payment.EndToEndId}",
        now,
        [
            new JournalLine(LedgerAccounts.PendingOutbound, Side.Debit, payment.Amount),
            new JournalLine(LedgerAccounts.Nostro, Side.Credit, payment.Amount),
        ]);

    public static JournalEntry Release(Payment payment, DateTimeOffset now) => new(
        Guid.NewGuid(),
        payment.Id,
        $"Release {payment.Amount} for {payment.EndToEndId} ({payment.ReasonCode})",
        now,
        [
            new JournalLine(LedgerAccounts.PendingOutbound, Side.Debit, payment.Amount),
            new JournalLine(LedgerAccounts.CustomerFunds, Side.Credit, payment.Amount),
        ]);

    public static JournalEntry Reverse(Payment payment, DateTimeOffset now) => new(
        Guid.NewGuid(),
        payment.Id,
        $"Return {payment.Amount} for {payment.EndToEndId} ({payment.ReasonCode})",
        now,
        [
            new JournalLine(LedgerAccounts.Nostro, Side.Debit, payment.Amount),
            new JournalLine(LedgerAccounts.CustomerFunds, Side.Credit, payment.Amount),
        ]);
}
