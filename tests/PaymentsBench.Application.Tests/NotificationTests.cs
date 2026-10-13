using PaymentsBench.Application.Payments;
using PaymentsBench.Application.Ports;
using PaymentsBench.Domain.Ledger;
using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Application.Tests;

public class NotificationTests
{
    [Fact]
    public async Task A_settlement_notification_settles_once_even_when_delivered_twice()
    {
        var host = new TestHost();
        var payment = await AcceptedPayment(host);
        var notification = new PartnerNotification("N-1", payment.EndToEndId, NotificationKind.Settled, null, new DateOnly(2026, 10, 14));

        var first = await host.Notify.HandleAsync(notification, CancellationToken.None);
        var second = await host.Notify.HandleAsync(notification, CancellationToken.None);

        Assert.Equal(NotificationOutcome.Applied, first);
        Assert.Equal(NotificationOutcome.Duplicate, second);
        var stored = await host.Payments.GetAsync(payment.Id, CancellationToken.None);
        Assert.Equal(PaymentStatus.Settled, stored!.Status);
        Assert.Equal(-100m, await host.Ledger.BalanceAsync(LedgerAccounts.Nostro, "EUR", CancellationToken.None));
        Assert.Equal(0m, await host.Ledger.BalanceAsync(LedgerAccounts.PendingOutbound, "EUR", CancellationToken.None));
    }

    [Fact]
    public async Task A_return_after_settlement_gives_the_customer_their_money_back()
    {
        var host = new TestHost();
        var payment = await AcceptedPayment(host);
        await host.Notify.HandleAsync(new PartnerNotification("N-1", payment.EndToEndId, NotificationKind.Settled, null, new DateOnly(2026, 10, 14)), CancellationToken.None);

        await host.Notify.HandleAsync(new PartnerNotification("N-2", payment.EndToEndId, NotificationKind.Returned, "AC04", null), CancellationToken.None);

        var stored = await host.Payments.GetAsync(payment.Id, CancellationToken.None);
        Assert.Equal(PaymentStatus.Returned, stored!.Status);
        Assert.Equal(0m, await host.Ledger.BalanceAsync(LedgerAccounts.CustomerFunds, "EUR", CancellationToken.None));
        Assert.Equal(0m, await host.Ledger.BalanceAsync(LedgerAccounts.Nostro, "EUR", CancellationToken.None));
    }

    [Fact]
    public async Task A_notification_for_an_unknown_payment_is_reported_not_dropped()
    {
        var host = new TestHost();

        var outcome = await host.Notify.HandleAsync(new PartnerNotification("N-9", "E2E-UNKNOWN", NotificationKind.Settled, null, null), CancellationToken.None);

        Assert.Equal(NotificationOutcome.UnknownPayment, outcome);
    }

    private static async Task<Payment> AcceptedPayment(TestHost host)
    {
        var payment = await host.Initiate.HandleAsync(TestHost.Command(), CancellationToken.None);
        host.Partner.SubmitAnswers.Enqueue(new SubmitResult(SubmitOutcome.Accepted, "P-1", null));
        await host.Submit.HandleAsync(new SubmitPayment(payment.Id), CancellationToken.None);
        return payment;
    }
}
