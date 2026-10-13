using Microsoft.EntityFrameworkCore;
using PaymentsBench.Domain.Ledger;
using PaymentsBench.Domain.Payments;
using PaymentsBench.Infrastructure.Persistence;

namespace PaymentsBench.Integration.Tests;

public sealed class UnitOfWorkTests(MigratedPostgresFixture database) : IClassFixture<MigratedPostgresFixture>
{
    [Fact]
    public async Task Payment_ledger_and_outbox_rows_commit_together()
    {
        var payment = Samples.PaymentIn(PaymentStatus.Initiated);
        await using (var write = database.CreateContext())
        {
            await Stage(write, payment);
            await new EfUnitOfWork(write).CommitAsync(CancellationToken.None);
        }

        var (payments, entries, messages) = await CountRowsFor(payment);
        Assert.Equal(1, payments);
        Assert.Equal(1, entries);
        Assert.Equal(1, messages);
    }

    [Fact]
    public async Task A_failing_commit_leaves_none_of_the_rows()
    {
        var first = Samples.PaymentIn(PaymentStatus.Initiated);
        var duplicate = Samples.PaymentIn(PaymentStatus.Initiated);
        var clashing = Payment.Initiate(
            duplicate.Id, first.IdempotencyKey, duplicate.RequestHash, duplicate.EndToEndId,
            duplicate.Amount, duplicate.DebtorAccount, duplicate.CreditorAccount, duplicate.Scheme, Samples.Start);

        await using (var write = database.CreateContext())
        {
            await Stage(write, first);
            await Stage(write, clashing);
            await Assert.ThrowsAsync<DbUpdateException>(() => new EfUnitOfWork(write).CommitAsync(CancellationToken.None));
        }

        foreach (var payment in new[] { first, clashing })
        {
            var (payments, entries, messages) = await CountRowsFor(payment);
            Assert.Equal(0, payments);
            Assert.Equal(0, entries);
            Assert.Equal(0, messages);
        }
    }

    private static async Task Stage(PaymentsDbContext db, Payment payment)
    {
        await new EfPaymentRepository(db).AddAsync(payment, CancellationToken.None);
        await new EfLedger(db).PostAsync(Postings.Reserve(payment, Samples.Start), CancellationToken.None);
        foreach (var paymentEvent in payment.UncommittedEvents)
        {
            await new EfOutbox(db).EnqueueAsync(paymentEvent, CancellationToken.None);
        }
    }

    private async Task<(int Payments, int Entries, int Messages)> CountRowsFor(Payment payment)
    {
        await using var read = database.CreateContext();
        return (
            await read.Payments.CountAsync(p => p.Id == payment.Id),
            await read.JournalEntries.CountAsync(e => e.PaymentId == payment.Id),
            await read.OutboxMessages.CountAsync(m => m.Payload.Contains(payment.Id.ToString())));
    }
}
