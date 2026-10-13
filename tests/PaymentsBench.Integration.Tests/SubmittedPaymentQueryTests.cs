using PaymentsBench.Application.Ports;
using PaymentsBench.Domain;
using PaymentsBench.Domain.Payments;
using PaymentsBench.Infrastructure.Persistence;

namespace PaymentsBench.Integration.Tests;

/// <summary>The sweep's query on a real PostgreSQL: cut-off, order and limit are applied in SQL.</summary>
public sealed class SubmittedPaymentQueryTests(MigratedPostgresFixture database) : IClassFixture<MigratedPostgresFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A payment at <paramref name="status"/> whose last update is exactly <paramref name="updatedAt"/>.</summary>
    private static Payment PaymentUpdatedAt(PaymentStatus status, DateTimeOffset updatedAt)
    {
        var payment = Payment.Initiate(
            Guid.NewGuid(), Samples.Unique("key"), "hash-" + Guid.NewGuid().ToString("N"), Samples.Unique("E2E"),
            new Money(10m, "EUR"), "DE89370400440532013000", "GB33BUKB20201555555555", "sepa", updatedAt.AddMinutes(-5));
        if (status != PaymentStatus.Initiated)
        {
            payment.MarkSubmitted(updatedAt);
        }

        if (status == PaymentStatus.Accepted)
        {
            payment.MarkAccepted("P-" + Guid.NewGuid().ToString("N"), updatedAt);
        }

        return payment;
    }

    private async Task StoreAsync(params Payment[] payments)
    {
        await using var write = database.CreateContext();
        var repository = new EfPaymentRepository(write);
        foreach (var payment in payments)
        {
            await repository.AddAsync(payment, CancellationToken.None);
        }

        await new EfUnitOfWork(write).CommitAsync(CancellationToken.None);
    }

    private async Task<SubmittedPaymentWindow> QueryAsync(DateTimeOffset cutoff, int limit)
    {
        await using var read = database.CreateContext();
        return await new EfPaymentRepository(read).ListSubmittedBeforeAsync(cutoff, limit, CancellationToken.None);
    }

    // One test on purpose: the class fixture is one database, and the oldest-Submitted assertions need it to hold only these rows.
    [Fact]
    public async Task Cut_off_order_and_limit_are_applied_to_submitted_payments_and_the_oldest_covers_them_all()
    {
        var oldest = PaymentUpdatedAt(PaymentStatus.Submitted, T0.AddMinutes(1));
        var middle = PaymentUpdatedAt(PaymentStatus.Submitted, T0.AddMinutes(2));
        var newest = PaymentUpdatedAt(PaymentStatus.Submitted, T0.AddMinutes(3));
        var atCutOff = PaymentUpdatedAt(PaymentStatus.Submitted, T0.AddMinutes(10));
        var accepted = PaymentUpdatedAt(PaymentStatus.Accepted, T0);
        var initiated = PaymentUpdatedAt(PaymentStatus.Initiated, T0);
        await StoreAsync(newest, accepted, atCutOff, oldest, initiated, middle);
        var cutoff = T0.AddMinutes(10);

        var all = await QueryAsync(cutoff, 100);
        var limited = await QueryAsync(cutoff, 2);
        var none = await QueryAsync(T0, 100);

        // Submitted only, strictly before the cut-off, oldest first.
        Assert.Equal([oldest.Id, middle.Id, newest.Id], all.Ids);
        Assert.Equal([oldest.Id, middle.Id], limited.Ids);

        // The oldest Submitted UpdatedAt is reported whatever the cut-off and limit, and ignores payments at other statuses.
        Assert.Equal(oldest.UpdatedAt, all.OldestUpdatedAt);
        Assert.Equal(oldest.UpdatedAt, limited.OldestUpdatedAt);
        Assert.Empty(none.Ids);
        Assert.Equal(oldest.UpdatedAt, none.OldestUpdatedAt);
    }
}
