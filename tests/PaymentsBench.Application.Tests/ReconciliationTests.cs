using PaymentsBench.Application.Payments;
using PaymentsBench.Application.Ports;
using PaymentsBench.Application.Reconciliation;

namespace PaymentsBench.Application.Tests;

public class ReconciliationTests
{
    private static readonly DateOnly Day = new(2026, 10, 14);

    [Fact]
    public async Task A_clean_day_has_no_breaks()
    {
        var host = new TestHost();
        await Settled(host, "k-1", "E2E-1", 100m);
        await Settled(host, "k-2", "E2E-2", 250m);
        var statement = new Statement(Day, "NOSTRO", [Line("E2E-1", 100m), Line("E2E-2", 250m)]);

        var report = await host.ReconcileWith(statement).ReconcileAsync(Day, CancellationToken.None);

        Assert.True(report.IsClean);
        Assert.Equal(2, report.Matched);
    }

    [Fact]
    public async Task Each_kind_of_break_is_named()
    {
        var host = new TestHost();
        await Settled(host, "k-1", "E2E-1", 100m);
        await Settled(host, "k-2", "E2E-2", 250m);
        var statement = new Statement(Day, "NOSTRO", [Line("E2E-1", 100m), Line("E2E-2", 255m), Line("E2E-3", 10m)]);

        var report = await host.ReconcileWith(statement).ReconcileAsync(Day, CancellationToken.None);

        Assert.Equal(1, report.Matched);
        Assert.Contains(report.Breaks, b => b.Kind == BreakKind.AmountMismatch && b.EndToEndId == "E2E-2" && b.StatementAmount == 255m && b.LedgerAmount == 250m);
        Assert.Contains(report.Breaks, b => b.Kind == BreakKind.OnStatementOnly && b.EndToEndId == "E2E-3");
        Assert.Equal(2, report.Breaks.Count);
    }

    [Fact]
    public async Task A_settled_payment_missing_from_the_statement_is_a_break()
    {
        var host = new TestHost();
        await Settled(host, "k-1", "E2E-1", 100m);
        var statement = new Statement(Day, "NOSTRO", []);

        var report = await host.ReconcileWith(statement).ReconcileAsync(Day, CancellationToken.None);

        Assert.Single(report.Breaks, b => b.Kind == BreakKind.InLedgerOnly && b.EndToEndId == "E2E-1");
    }

    private static StatementLine Line(string endToEndId, decimal amount) => new(endToEndId, amount, "EUR", Day, StatementDirection.Debit);

    private static async Task Settled(TestHost host, string key, string endToEndId, decimal amount)
    {
        var payment = await host.Initiate.HandleAsync(TestHost.Command(key, endToEndId, amount), CancellationToken.None);
        host.Partner.SubmitAnswers.Enqueue(new SubmitResult(SubmitOutcome.Accepted, "P", null));
        await host.Submit.HandleAsync(new SubmitPayment(payment.Id), CancellationToken.None);
        await host.Notify.HandleAsync(new PartnerNotification($"N-{endToEndId}", endToEndId, NotificationKind.Settled, null, Day), CancellationToken.None);
    }
}
