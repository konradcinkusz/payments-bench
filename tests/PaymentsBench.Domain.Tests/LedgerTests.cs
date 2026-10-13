using PaymentsBench.Domain;
using PaymentsBench.Domain.Ledger;
using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Domain.Tests;

public class LedgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_unbalanced_entry_cannot_exist()
    {
        var lines = new List<JournalLine>
        {
            new(LedgerAccounts.CustomerFunds, Side.Debit, new Money(100, "EUR")),
            new(LedgerAccounts.PendingOutbound, Side.Credit, new Money(99, "EUR")),
        };

        Assert.Throws<DomainException>(() => new JournalEntry(Guid.NewGuid(), Guid.NewGuid(), "x", Now, lines));
    }

    [Fact]
    public void Balance_is_checked_per_currency()
    {
        var lines = new List<JournalLine>
        {
            new(LedgerAccounts.CustomerFunds, Side.Debit, new Money(100, "EUR")),
            new(LedgerAccounts.PendingOutbound, Side.Credit, new Money(100, "GBP")),
        };

        Assert.Throws<DomainException>(() => new JournalEntry(Guid.NewGuid(), Guid.NewGuid(), "x", Now, lines));
    }

    [Fact]
    public void Direction_is_the_side_never_a_negative_amount()
    {
        Assert.Throws<DomainException>(() => new JournalLine("a", Side.Debit, new Money(-5, "EUR")) is var line
            && new JournalEntry(Guid.NewGuid(), Guid.NewGuid(), "x", Now, [line, new JournalLine("b", Side.Credit, new Money(-5, "EUR"))]) is not null);
    }

    [Fact]
    public void The_full_money_path_of_a_returned_payment_nets_to_zero_on_customer_funds_and_nostro()
    {
        var payment = Payment.Initiate(Guid.NewGuid(), "k", "h", "E2E", new Money(250, "EUR"), "DE1", "DE2", "sepa", Now);
        payment.MarkSubmitted(Now);
        payment.MarkAccepted("P", Now);
        payment.MarkSettled(new DateOnly(2026, 10, 14), Now);
        payment.MarkReturned("AC04", Now);

        var entries = new[] { Postings.Reserve(payment, Now), Postings.Settle(payment, Now), Postings.Reverse(payment, Now) };

        Assert.Equal(0, Balance(entries, LedgerAccounts.CustomerFunds));
        Assert.Equal(0, Balance(entries, LedgerAccounts.PendingOutbound));
        Assert.Equal(0, Balance(entries, LedgerAccounts.Nostro));
    }

    [Fact]
    public void A_rejected_payment_releases_the_reservation()
    {
        var payment = Payment.Initiate(Guid.NewGuid(), "k", "h", "E2E", new Money(250, "EUR"), "DE1", "DE2", "sepa", Now);
        payment.MarkSubmitted(Now);
        payment.MarkRejected("AC01", Now);

        var entries = new[] { Postings.Reserve(payment, Now), Postings.Release(payment, Now) };

        Assert.Equal(0, Balance(entries, LedgerAccounts.CustomerFunds));
        Assert.Equal(0, Balance(entries, LedgerAccounts.PendingOutbound));
    }

    private static decimal Balance(IEnumerable<JournalEntry> entries, string account) =>
        entries.SelectMany(e => e.Lines).Where(l => l.Account == account).Sum(l => l.Side == Side.Debit ? l.Amount.Amount : -l.Amount.Amount);
}
