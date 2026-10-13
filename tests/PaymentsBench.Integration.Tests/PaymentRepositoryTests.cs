using PaymentsBench.Domain.Payments;
using PaymentsBench.Infrastructure.Persistence;

namespace PaymentsBench.Integration.Tests;

public sealed class PaymentRepositoryTests(MigratedPostgresFixture database) : IClassFixture<MigratedPostgresFixture>
{
    public static TheoryData<PaymentStatus> AllStatuses => new(Enum.GetValues<PaymentStatus>());

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public async Task A_payment_in_every_status_reads_back_with_all_fields_equal(PaymentStatus status)
    {
        var original = Samples.PaymentIn(status);
        await using (var write = database.CreateContext())
        {
            await new EfPaymentRepository(write).AddAsync(original, CancellationToken.None);
            await new EfUnitOfWork(write).CommitAsync(CancellationToken.None);
        }

        await using var read = database.CreateContext();
        var loaded = await new EfPaymentRepository(read).GetAsync(original.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(original.Id, loaded.Id);
        Assert.Equal(original.IdempotencyKey, loaded.IdempotencyKey);
        Assert.Equal(original.RequestHash, loaded.RequestHash);
        Assert.Equal(original.EndToEndId, loaded.EndToEndId);
        Assert.Equal(original.Amount, loaded.Amount);
        Assert.Equal(original.DebtorAccount, loaded.DebtorAccount);
        Assert.Equal(original.CreditorAccount, loaded.CreditorAccount);
        Assert.Equal(original.Scheme, loaded.Scheme);
        Assert.Equal(original.Status, loaded.Status);
        Assert.Equal(original.ReasonCode, loaded.ReasonCode);
        Assert.Equal(original.PartnerReference, loaded.PartnerReference);
        Assert.Equal(original.ValueDate, loaded.ValueDate);
        Assert.Equal(original.CreatedAt, loaded.CreatedAt);
        Assert.Equal(original.UpdatedAt, loaded.UpdatedAt);
        Assert.Empty(loaded.UncommittedEvents);
    }

    [Fact]
    public async Task A_missing_payment_is_null()
    {
        await using var read = database.CreateContext();
        Assert.Null(await new EfPaymentRepository(read).GetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task A_payment_is_found_by_idempotency_key_and_by_end_to_end_id()
    {
        var original = Samples.PaymentIn(PaymentStatus.Accepted);
        await Save(original);

        await using var read = database.CreateContext();
        var repository = new EfPaymentRepository(read);
        var byKey = await repository.FindByIdempotencyKeyAsync(original.IdempotencyKey, CancellationToken.None);
        var byEndToEnd = await repository.FindByEndToEndIdAsync(original.EndToEndId, CancellationToken.None);

        Assert.Equal(original.Id, byKey?.Id);
        Assert.Equal(original.Id, byEndToEnd?.Id);
        Assert.Null(await repository.FindByIdempotencyKeyAsync("no-such-key", CancellationToken.None));
        Assert.Null(await repository.FindByEndToEndIdAsync("no-such-e2e", CancellationToken.None));
    }

    [Fact]
    public async Task Payments_are_listed_by_status()
    {
        var submitted = new[] { Samples.PaymentIn(PaymentStatus.Submitted), Samples.PaymentIn(PaymentStatus.Submitted) };
        var settled = Samples.PaymentIn(PaymentStatus.Settled);
        foreach (var payment in submitted.Append(settled))
        {
            await Save(payment);
        }

        await using var read = database.CreateContext();
        var listed = await new EfPaymentRepository(read).ListByStatusAsync(PaymentStatus.Submitted, CancellationToken.None);

        Assert.All(listed, p => Assert.Equal(PaymentStatus.Submitted, p.Status));
        Assert.Subset(listed.Select(p => p.Id).ToHashSet(), submitted.Select(p => p.Id).ToHashSet());
        Assert.DoesNotContain(listed, p => p.Id == settled.Id);
    }

    [Fact]
    public async Task An_update_persists_the_new_status_and_bumps_the_version()
    {
        var payment = Samples.PaymentIn(PaymentStatus.Initiated);
        await Save(payment);

        await using (var write = database.CreateContext())
        {
            var repository = new EfPaymentRepository(write);
            var loaded = await repository.GetAsync(payment.Id, CancellationToken.None);
            loaded!.MarkSubmitted(Samples.Start.AddMinutes(5));
            await repository.UpdateAsync(loaded, CancellationToken.None);
            await new EfUnitOfWork(write).CommitAsync(CancellationToken.None);
        }

        await using var read = database.CreateContext();
        var row = await read.Payments.FindAsync(payment.Id);
        Assert.Equal((int)PaymentStatus.Submitted, row!.Status);
        Assert.Equal(1, row.Version);
    }

    private async Task Save(Payment payment)
    {
        await using var write = database.CreateContext();
        await new EfPaymentRepository(write).AddAsync(payment, CancellationToken.None);
        await new EfUnitOfWork(write).CommitAsync(CancellationToken.None);
    }
}
