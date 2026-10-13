using Microsoft.EntityFrameworkCore;
using PaymentsBench.Application.Payments;
using PaymentsBench.Application.Ports;
using PaymentsBench.Domain.Ledger;
using PaymentsBench.Domain.Payments;
using PaymentsBench.Infrastructure.Persistence;
using PaymentsBench.Infrastructure.Schemes;

namespace PaymentsBench.Integration.Tests;

/// <summary>
/// Optimistic concurrency on PostgreSQL (P4). The version a payment was read at is the version the
/// update checks, so of two writers on one payment exactly one wins and the other fails loudly.
/// The partner is simulated.
/// </summary>
public sealed class ConcurrencyTests(MigratedPostgresFixture database) : IClassFixture<MigratedPostgresFixture>
{
    private static readonly ISchemeCatalog Schemes = SchemeCatalogLoader.Load(Path.Join(AppContext.BaseDirectory, "scenarios"));

    private readonly FixedClock _clock = new(Samples.Start);

    [Fact]
    public async Task Two_writers_that_read_the_same_payment_cannot_both_win()
    {
        var payment = await Save(Samples.PaymentIn(PaymentStatus.Initiated));

        // Both scopes read before either writes: each signals it has read, then waits for the other.
        var firstRead = new TaskCompletionSource();
        var secondRead = new TaskCompletionSource();

        // An unknown partner outcome leaves the payment at Submitted after one commit, so a winner bumps Version once.
        var partner = new ScriptedPartner();
        partner.SubmitAnswers.Enqueue(new SubmitResult(SubmitOutcome.Unknown, null, null));
        var submit = Task.Run(async () =>
        {
            await using var db = database.CreateContext();
            var repository = new EfPaymentRepository(db);
            var handler = new SubmitPaymentHandler(new GatedRepository(repository, firstRead, secondRead), partner, new EfLedger(db), new EfOutbox(db), new EfUnitOfWork(db), _clock);
            await handler.HandleAsync(new SubmitPayment(payment.Id), CancellationToken.None);
        });
        var reject = Task.Run(async () =>
        {
            await using var db = database.CreateContext();
            var repository = new GatedRepository(new EfPaymentRepository(db), secondRead, firstRead);
            var loaded = await repository.GetAsync(payment.Id, CancellationToken.None);
            loaded!.MarkSubmitted(_clock.UtcNow);
            loaded.MarkRejected("AC04", _clock.UtcNow);
            await repository.UpdateAsync(loaded, CancellationToken.None);
            foreach (var paymentEvent in loaded.UncommittedEvents)
            {
                await new EfOutbox(db).EnqueueAsync(paymentEvent, CancellationToken.None);
            }

            await new EfLedger(db).PostAsync(Postings.Release(loaded, _clock.UtcNow), CancellationToken.None);
            await new EfUnitOfWork(db).CommitAsync(CancellationToken.None);
        });

        var outcomes = await Task.WhenAll(Settle(submit), Settle(reject));

        Assert.Single(outcomes, e => e is null);
        Assert.IsType<ConcurrencyConflictException>(Assert.Single(outcomes, e => e is not null));

        await using var read = database.CreateContext();
        var row = await read.Payments.SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(1, row.Version);

        var winnerWasSubmit = outcomes[0] is null;
        if (winnerWasSubmit)
        {
            Assert.Equal((int)PaymentStatus.Submitted, row.Status);
            Assert.Single(partner.Submits);
            Assert.Empty(await read.JournalEntries.Where(e => e.PaymentId == payment.Id).ToListAsync());
            Assert.Equal([nameof(PaymentSubmitted)], await OutboxTypesFor(read, payment.Id, excluding: nameof(PaymentInitiated)));
        }
        else
        {
            Assert.Equal((int)PaymentStatus.Rejected, row.Status);
            Assert.Empty(partner.Submits);
            Assert.Single(await read.JournalEntries.Where(e => e.PaymentId == payment.Id).ToListAsync());
            Assert.Equal([nameof(PaymentSubmitted), nameof(PaymentRejected)], await OutboxTypesFor(read, payment.Id, excluding: nameof(PaymentInitiated)));
        }
    }

    [Fact]
    public async Task A_writer_that_read_before_another_committed_fails_instead_of_overwriting()
    {
        var payment = await Save(Samples.PaymentIn(PaymentStatus.Initiated));

        await using var staleScope = database.CreateContext();
        var staleRepository = new EfPaymentRepository(staleScope);
        var stale = await staleRepository.GetAsync(payment.Id, CancellationToken.None);

        await using (var winnerScope = database.CreateContext())
        {
            var winnerRepository = new EfPaymentRepository(winnerScope);
            var winner = await winnerRepository.GetAsync(payment.Id, CancellationToken.None);
            winner!.MarkSubmitted(_clock.UtcNow);
            winner.MarkRejected("AC04", _clock.UtcNow);
            await winnerRepository.UpdateAsync(winner, CancellationToken.None);
            await new EfUnitOfWork(winnerScope).CommitAsync(CancellationToken.None);
        }

        // The old defect: the update re-read the row, saw version 1 and overwrote the rejection.
        stale!.MarkSubmitted(_clock.UtcNow);
        await staleRepository.UpdateAsync(stale, CancellationToken.None);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => new EfUnitOfWork(staleScope).CommitAsync(CancellationToken.None));

        await using var read = database.CreateContext();
        var row = await read.Payments.SingleAsync(p => p.Id == payment.Id);
        Assert.Equal((int)PaymentStatus.Rejected, row.Status);
        Assert.Equal(1, row.Version);
        Assert.Empty(staleScope.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Updating_the_same_payment_twice_in_one_scope_succeeds()
    {
        var payment = await Save(Samples.PaymentIn(PaymentStatus.Initiated));

        await using (var db = database.CreateContext())
        {
            var repository = new EfPaymentRepository(db);
            var unitOfWork = new EfUnitOfWork(db);
            var loaded = await repository.GetAsync(payment.Id, CancellationToken.None);

            loaded!.MarkSubmitted(_clock.UtcNow);
            await repository.UpdateAsync(loaded, CancellationToken.None);
            await unitOfWork.CommitAsync(CancellationToken.None);

            loaded.MarkAccepted("P-1", _clock.UtcNow);
            await repository.UpdateAsync(loaded, CancellationToken.None);
            await unitOfWork.CommitAsync(CancellationToken.None);
        }

        await using var read = database.CreateContext();
        var row = await read.Payments.SingleAsync(p => p.Id == payment.Id);
        Assert.Equal((int)PaymentStatus.Accepted, row.Status);
        Assert.Equal(2, row.Version);
    }

    [Fact]
    public async Task Updating_a_payment_that_was_not_read_in_the_scope_is_a_programming_error()
    {
        var payment = await Save(Samples.PaymentIn(PaymentStatus.Initiated));
        await using var db = database.CreateContext();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new EfPaymentRepository(db).UpdateAsync(payment, CancellationToken.None));
        Assert.Contains(payment.Id.ToString(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_concurrent_initiations_with_one_key_leave_one_payment_and_one_conflict()
    {
        var key = Samples.Unique("race");
        var endToEnd = Samples.Unique("E2E");
        var command = new InitiatePayment(key, endToEnd, 10m, "EUR", "DE89370400440532013000", "GB33BUKB20201555555555", "sepa");

        // Both scopes find no payment under the key, then both insert.
        var firstChecked = new TaskCompletionSource();
        var secondChecked = new TaskCompletionSource();
        var first = Task.Run(() => InitiateGated(command, firstChecked, secondChecked));
        var second = Task.Run(() => InitiateGated(command, secondChecked, firstChecked));

        var outcomes = await Task.WhenAll(Settle(first), Settle(second));

        Assert.Single(outcomes, e => e is null);
        Assert.IsType<ConcurrencyConflictException>(Assert.Single(outcomes, e => e is not null));
        await using var read = database.CreateContext();
        Assert.Equal(1, await read.Payments.CountAsync(p => p.IdempotencyKey == key));
        var id = (await read.Payments.SingleAsync(p => p.IdempotencyKey == key)).Id;
        Assert.Single(await read.JournalEntries.Where(e => e.PaymentId == id).ToListAsync());
    }

    private async Task InitiateGated(InitiatePayment command, TaskCompletionSource mine, TaskCompletionSource theirs)
    {
        await using var db = database.CreateContext();
        var repository = new GatedRepository(new EfPaymentRepository(db), mine, theirs);
        var handler = new InitiatePaymentHandler(repository, new EfLedger(db), new EfOutbox(db), new EfUnitOfWork(db), _clock, Schemes);
        await handler.HandleAsync(command, CancellationToken.None);
    }

    /// <summary>Null when the writer committed, the conflict when it lost; any other failure propagates and fails the test.</summary>
    private static async Task<ConcurrencyConflictException?> Settle(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (ConcurrencyConflictException e)
        {
            return e;
        }
    }

    private async Task<Payment> Save(Payment payment)
    {
        await using var write = database.CreateContext();
        await new EfPaymentRepository(write).AddAsync(payment, CancellationToken.None);
        await new EfUnitOfWork(write).CommitAsync(CancellationToken.None);
        return payment;
    }

    private static async Task<string[]> OutboxTypesFor(PaymentsDbContext db, Guid paymentId, string excluding)
    {
        var rows = await db.OutboxMessages.Where(m => m.Payload.Contains(paymentId.ToString()) && m.Type != excluding).ToListAsync();
        return [.. rows.OrderBy(m => m.OccurredAt).ThenBy(m => m.Type == nameof(PaymentSubmitted) ? 0 : 1).Select(m => m.Type)];
    }

    /// <summary>
    /// Holds each reader at a barrier after its first read: it signals that it has read, then waits
    /// until the other scope has too. No sleeps, so both reads happen before either write.
    /// </summary>
    private sealed class GatedRepository(IPaymentRepository inner, TaskCompletionSource mine, TaskCompletionSource theirs) : IPaymentRepository
    {
        public async Task<Payment?> GetAsync(Guid id, CancellationToken cancellationToken) => await Gate(await inner.GetAsync(id, cancellationToken));

        public async Task<Payment?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
            await Gate(await inner.FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken));

        public Task<Payment?> FindByEndToEndIdAsync(string endToEndId, CancellationToken cancellationToken) => inner.FindByEndToEndIdAsync(endToEndId, cancellationToken);

        public Task<IReadOnlyList<Payment>> ListByStatusAsync(PaymentStatus status, CancellationToken cancellationToken) => inner.ListByStatusAsync(status, cancellationToken);

        public Task<PaymentPage> ListAsync(PaymentQuery query, CancellationToken cancellationToken) => inner.ListAsync(query, cancellationToken);

        public Task AddAsync(Payment payment, CancellationToken cancellationToken) => inner.AddAsync(payment, cancellationToken);

        public Task UpdateAsync(Payment payment, CancellationToken cancellationToken) => inner.UpdateAsync(payment, cancellationToken);

        private async Task<Payment?> Gate(Payment? result)
        {
            mine.TrySetResult();
            await theirs.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return result;
        }
    }
}
