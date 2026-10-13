using PaymentsBench.Application.Payments;
using PaymentsBench.Domain.Ledger;

namespace PaymentsBench.Application.Tests;

public class IdempotencyTests
{
    [Fact]
    public async Task The_same_request_twice_creates_one_payment_and_one_reservation()
    {
        var host = new TestHost();

        var first = await host.Initiate.HandleAsync(TestHost.Command(), CancellationToken.None);
        var second = await host.Initiate.HandleAsync(TestHost.Command(), CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(host.Ledger.Entries);
        Assert.Single(host.Outbox.Events);
        Assert.Equal(-100m, await host.Ledger.BalanceAsync(LedgerAccounts.PendingOutbound, "EUR", CancellationToken.None));
    }

    [Fact]
    public async Task The_same_key_with_a_different_body_is_a_conflict_not_a_second_payment()
    {
        var host = new TestHost();
        await host.Initiate.HandleAsync(TestHost.Command(), CancellationToken.None);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            host.Initiate.HandleAsync(TestHost.Command(amount: 101m), CancellationToken.None));

        Assert.Single(host.Ledger.Entries);
    }

    [Fact]
    public async Task Different_keys_are_different_payments()
    {
        var host = new TestHost();

        var a = await host.Initiate.HandleAsync(TestHost.Command("k-a", "E2E-A"), CancellationToken.None);
        var b = await host.Initiate.HandleAsync(TestHost.Command("k-b", "E2E-B"), CancellationToken.None);

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(2, host.Ledger.Entries.Count);
    }
}
