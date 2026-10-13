using PaymentsBench.Application.Payments;
using PaymentsBench.Application.Ports;
using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Application.Tests;

/// <summary>The degraded in-memory store follows the PostgreSQL rule: an update checks the version the payment was read at.</summary>
public class InMemoryConcurrencyTests
{
    [Fact]
    public async Task A_stale_update_fails_and_leaves_the_winner_in_place()
    {
        var host = new TestHost();
        var created = await host.Initiate.HandleAsync(TestHost.Command(), CancellationToken.None);

        var winner = await host.Payments.GetAsync(created.Id, CancellationToken.None);
        var loser = await host.Payments.GetAsync(created.Id, CancellationToken.None);
        winner!.MarkSubmitted(TestHost.Start);
        winner.MarkRejected("AC04", TestHost.Start);
        await host.Payments.UpdateAsync(winner, CancellationToken.None);

        loser!.MarkSubmitted(TestHost.Start);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => host.Payments.UpdateAsync(loser, CancellationToken.None));

        var stored = await host.Payments.GetAsync(created.Id, CancellationToken.None);
        Assert.Equal(PaymentStatus.Rejected, stored!.Status);
    }

    [Fact]
    public async Task Updating_twice_through_one_instance_succeeds()
    {
        var host = new TestHost();
        var created = await host.Initiate.HandleAsync(TestHost.Command(), CancellationToken.None);

        var payment = await host.Payments.GetAsync(created.Id, CancellationToken.None);
        payment!.MarkSubmitted(TestHost.Start);
        await host.Payments.UpdateAsync(payment, CancellationToken.None);
        payment.MarkAccepted("P-1", TestHost.Start);
        await host.Payments.UpdateAsync(payment, CancellationToken.None);

        Assert.Equal(PaymentStatus.Accepted, (await host.Payments.GetAsync(created.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task A_payment_that_was_not_read_from_the_store_cannot_be_updated()
    {
        var host = new TestHost();
        var created = await host.Initiate.HandleAsync(TestHost.Command(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Payments.UpdateAsync(created, CancellationToken.None));
    }

    [Fact]
    public async Task A_second_payment_with_the_same_idempotency_key_is_a_conflict()
    {
        var host = new TestHost();
        var created = await host.Initiate.HandleAsync(TestHost.Command(), CancellationToken.None);
        var clash = Payment.Initiate(
            Guid.NewGuid(), created.IdempotencyKey, "hash", "E2E-other", created.Amount,
            created.DebtorAccount, created.CreditorAccount, created.Scheme, TestHost.Start);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => host.Payments.AddAsync(clash, CancellationToken.None));
    }
}
