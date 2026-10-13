using Microsoft.EntityFrameworkCore;
using PaymentsBench.Application.Payments;
using PaymentsBench.Application.Ports;
using PaymentsBench.Domain.Ledger;
using PaymentsBench.Domain.Payments;
using PaymentsBench.Infrastructure.Persistence;

namespace PaymentsBench.Integration.Tests;

/// <summary>The application handlers on the EF Core stores, one scoped context per call as in the API. The partner is simulated.</summary>
public sealed class HandlerTests(MigratedPostgresFixture database) : IClassFixture<MigratedPostgresFixture>
{
    private readonly FixedClock _clock = new(Samples.Start);

    [Fact]
    public async Task Initiate_then_submit_accepted_leaves_status_ledger_and_outbox_in_the_database()
    {
        var partner = new ScriptedPartner();
        var customerBefore = await Balance(LedgerAccounts.CustomerFunds);
        var pendingBefore = await Balance(LedgerAccounts.PendingOutbound);

        var created = await Initiate("accepted", amount: 250m);
        await Submit(created.Id, partner);

        await using var read = database.CreateContext();
        var row = await read.Payments.SingleAsync(p => p.Id == created.Id);
        Assert.Equal((int)PaymentStatus.Accepted, row.Status);
        Assert.Equal("P-1", row.PartnerReference);
        Assert.Equal(2, row.Version);
        Assert.Equal(created.EndToEndId, Assert.Single(partner.Submits).EndToEndId);

        Assert.Equal(250m, await Balance(LedgerAccounts.CustomerFunds) - customerBefore);
        Assert.Equal(-250m, await Balance(LedgerAccounts.PendingOutbound) - pendingBefore);
        Assert.Equal(
            [nameof(PaymentInitiated), nameof(PaymentSubmitted), nameof(PaymentAccepted)],
            await OutboxTypesFor(created.Id));
    }

    [Fact]
    public async Task Initiate_then_submit_rejected_releases_the_reserve()
    {
        var partner = new ScriptedPartner();
        partner.SubmitAnswers.Enqueue(new SubmitResult(SubmitOutcome.Rejected, null, "AC04"));
        var customerBefore = await Balance(LedgerAccounts.CustomerFunds);
        var pendingBefore = await Balance(LedgerAccounts.PendingOutbound);

        var created = await Initiate("rejected", amount: 80m);
        await Submit(created.Id, partner);

        await using var read = database.CreateContext();
        var row = await read.Payments.SingleAsync(p => p.Id == created.Id);
        Assert.Equal((int)PaymentStatus.Rejected, row.Status);
        Assert.Equal("AC04", row.ReasonCode);

        Assert.Equal(0m, await Balance(LedgerAccounts.CustomerFunds) - customerBefore);
        Assert.Equal(0m, await Balance(LedgerAccounts.PendingOutbound) - pendingBefore);
        Assert.Equal(2, (await new EfLedger(read).EntriesForAsync(created.Id, CancellationToken.None)).Count);
        Assert.Equal(
            [nameof(PaymentInitiated), nameof(PaymentSubmitted), nameof(PaymentRejected)],
            await OutboxTypesFor(created.Id));
    }

    [Fact]
    public async Task Initiating_twice_with_the_same_key_creates_one_payment()
    {
        var first = await Initiate("replay", amount: 10m);
        var second = await Initiate("replay", amount: 10m);

        Assert.Equal(first.Id, second.Id);
        await using var read = database.CreateContext();
        Assert.Equal(1, await read.Payments.CountAsync(p => p.IdempotencyKey == first.IdempotencyKey));
    }

    private async Task<Payment> Initiate(string name, decimal amount)
    {
        await using var db = database.CreateContext();
        var handler = new InitiatePaymentHandler(new EfPaymentRepository(db), new EfLedger(db), new EfOutbox(db), new EfUnitOfWork(db), _clock);
        return await handler.HandleAsync(
            new InitiatePayment(KeyFor(name), EndToEndFor(name), amount, "EUR", "DE89370400440532013000", "GB33BUKB20201555555555", "sepa"),
            CancellationToken.None);
    }

    private async Task Submit(Guid paymentId, ScriptedPartner partner)
    {
        await using var db = database.CreateContext();
        var handler = new SubmitPaymentHandler(new EfPaymentRepository(db), partner, new EfLedger(db), new EfOutbox(db), new EfUnitOfWork(db), _clock);
        await handler.HandleAsync(new SubmitPayment(paymentId), CancellationToken.None);
    }

    private async Task<decimal> Balance(string account)
    {
        await using var db = database.CreateContext();
        return await new EfLedger(db).BalanceAsync(account, "EUR", CancellationToken.None);
    }

    private async Task<string[]> OutboxTypesFor(Guid paymentId)
    {
        await using var db = database.CreateContext();
        var rows = await db.OutboxMessages.Where(m => m.Payload.Contains(paymentId.ToString())).ToListAsync();
        return [.. rows.OrderBy(m => m.OccurredAt).ThenBy(m => m.Type == nameof(PaymentInitiated) ? 0 : m.Type == nameof(PaymentSubmitted) ? 1 : 2).Select(m => m.Type)];
    }

    // Stable per test instance, so a replay in the same test reuses the key; unique across tests.
    private readonly string _run = Guid.NewGuid().ToString("N");

    private string KeyFor(string name) => $"key-{name}-{_run}";

    private string EndToEndFor(string name) => $"E2E-{name}-{_run}";
}
