using PaymentsBench.Domain;
using PaymentsBench.Domain.Ledger;
using PaymentsBench.Infrastructure.Persistence;

namespace PaymentsBench.Integration.Tests;

public sealed class LedgerTests(MigratedPostgresFixture database) : IClassFixture<MigratedPostgresFixture>
{
    [Fact]
    public async Task A_posted_entry_reads_back_with_its_lines()
    {
        var paymentId = Guid.NewGuid();
        var funds = Samples.Unique("funds");
        var pending = Samples.Unique("pending");
        var entry = new JournalEntry(Guid.NewGuid(), paymentId, "Reserve 100.25 EUR", Samples.Start,
            [new JournalLine(funds, Side.Debit, new Money(100.25m, "EUR")), new JournalLine(pending, Side.Credit, new Money(100.25m, "EUR"))]);

        await Post(entry);

        await using var read = database.CreateContext();
        var entries = await new EfLedger(read).EntriesForAsync(paymentId, CancellationToken.None);
        var loaded = Assert.Single(entries);
        Assert.Equal(entry.Id, loaded.Id);
        Assert.Equal(entry.Description, loaded.Description);
        Assert.Equal(entry.PostedAt, loaded.PostedAt);
        Assert.Equal(entry.Lines.OrderBy(l => l.Account), loaded.Lines.OrderBy(l => l.Account));
    }

    [Fact]
    public async Task Balances_are_derived_per_account_and_currency()
    {
        var funds = Samples.Unique("funds");
        var pending = Samples.Unique("pending");
        var payment = Guid.NewGuid();
        await Post(Entry(payment, funds, pending, 100m, "EUR"));
        await Post(Entry(payment, funds, pending, 40.50m, "EUR"));
        await Post(Entry(payment, pending, funds, 25m, "EUR"));
        await Post(Entry(payment, funds, pending, 7m, "GBP"));

        await using var read = database.CreateContext();
        var ledger = new EfLedger(read);

        Assert.Equal(115.50m, await ledger.BalanceAsync(funds, "EUR", CancellationToken.None));
        Assert.Equal(-115.50m, await ledger.BalanceAsync(pending, "EUR", CancellationToken.None));
        Assert.Equal(7m, await ledger.BalanceAsync(funds, "GBP", CancellationToken.None));
        Assert.Equal(0m, await ledger.BalanceAsync(Samples.Unique("untouched"), "EUR", CancellationToken.None));
    }

    private static JournalEntry Entry(Guid paymentId, string debit, string credit, decimal amount, string currency) =>
        new(Guid.NewGuid(), paymentId, "test posting", Samples.Start,
            [new JournalLine(debit, Side.Debit, new Money(amount, currency)), new JournalLine(credit, Side.Credit, new Money(amount, currency))]);

    private async Task Post(JournalEntry entry)
    {
        await using var write = database.CreateContext();
        await new EfLedger(write).PostAsync(entry, CancellationToken.None);
        await new EfUnitOfWork(write).CommitAsync(CancellationToken.None);
    }
}
