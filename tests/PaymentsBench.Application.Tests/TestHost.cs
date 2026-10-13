using PaymentsBench.Application.Payments;
using PaymentsBench.Application.Reconciliation;
using PaymentsBench.Infrastructure.InMemory;

namespace PaymentsBench.Application.Tests;

/// <summary>Every handler wired to the in-memory stores and a scripted partner, the way the API wires them to the real ones.</summary>
public sealed class TestHost
{
    public static readonly DateTimeOffset Start = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    public FixedClock Clock { get; } = new(Start);

    public InMemoryPaymentRepository Payments { get; } = new();

    public InMemoryLedger Ledger { get; } = new();

    public InMemoryOutbox Outbox { get; } = new();

    public InMemoryInbox Inbox { get; } = new();

    public NoOpUnitOfWork UnitOfWork { get; } = new();

    public ScriptedPartner Partner { get; } = new();

    public InitiatePaymentHandler Initiate => new(Payments, Ledger, Outbox, UnitOfWork, Clock);

    public SubmitPaymentHandler Submit => new(Payments, Partner, Ledger, Outbox, UnitOfWork, Clock);

    public ResolveSubmittedPaymentHandler Resolve => new(Payments, Partner, Ledger, Outbox, UnitOfWork, Clock);

    public ApplyPartnerNotificationHandler Notify => new(Payments, Inbox, Ledger, Outbox, UnitOfWork, Clock);

    public ReconciliationService ReconcileWith(Ports.Statement statement) => new(Payments, new FixedStatements(statement));

    public static InitiatePayment Command(string key = "key-1", string endToEndId = "E2E-1", decimal amount = 100m) =>
        new(key, endToEndId, amount, "EUR", "DE89370400440532013000", "GB33BUKB20201555555555", "sepa");
}
