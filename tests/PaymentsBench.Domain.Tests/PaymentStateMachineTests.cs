using PaymentsBench.Domain;
using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Domain.Tests;

public class PaymentStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Initiate_starts_in_initiated_and_records_the_event()
    {
        var payment = NewPayment();

        Assert.Equal(PaymentStatus.Initiated, payment.Status);
        Assert.Single(payment.UncommittedEvents.OfType<PaymentInitiated>());
    }

    [Fact]
    public void Happy_path_runs_initiated_submitted_accepted_settled()
    {
        var payment = NewPayment();

        payment.MarkSubmitted(Now);
        payment.MarkAccepted("P-1", Now);
        payment.MarkSettled(new DateOnly(2026, 10, 14), Now);

        Assert.Equal(PaymentStatus.Settled, payment.Status);
        Assert.Equal("P-1", payment.PartnerReference);
        Assert.Equal(new DateOnly(2026, 10, 14), payment.ValueDate);
    }

    [Fact]
    public void A_settled_payment_can_be_returned_but_not_rejected()
    {
        var payment = NewPayment();
        payment.MarkSubmitted(Now);
        payment.MarkAccepted("P-1", Now);
        payment.MarkSettled(new DateOnly(2026, 10, 14), Now);

        Assert.Throws<InvalidTransitionException>(() => payment.MarkRejected("AC01", Now));
        payment.MarkReturned("AC04", Now);

        Assert.Equal(PaymentStatus.Returned, payment.Status);
        Assert.Equal("AC04", payment.ReasonCode);
    }

    [Fact]
    public void Terminal_states_accept_no_move()
    {
        foreach (var status in Enum.GetValues<PaymentStatus>().Where(PaymentTransitions.IsTerminal))
        {
            Assert.DoesNotContain(PaymentTransitions.All(), t => t.From == status);
        }

        Assert.Equal(new[] { PaymentStatus.Rejected, PaymentStatus.Returned }, Enum.GetValues<PaymentStatus>().Where(PaymentTransitions.IsTerminal));
    }

    [Fact]
    public void Nothing_can_be_settled_or_accepted_before_being_submitted()
    {
        var payment = NewPayment();

        Assert.Throws<InvalidTransitionException>(() => payment.MarkAccepted("P-1", Now));
        Assert.Throws<InvalidTransitionException>(() => payment.MarkSettled(new DateOnly(2026, 10, 14), Now));
        Assert.Throws<InvalidTransitionException>(() => payment.MarkRejected("AC01", Now));
    }

    [Fact]
    public void Every_failure_needs_a_reason_code()
    {
        var payment = NewPayment();
        payment.MarkSubmitted(Now);

        Assert.Throws<DomainException>(() => payment.MarkRejected(" ", Now));
        Assert.Equal(PaymentStatus.Submitted, payment.Status);
    }

    [Fact]
    public void Amount_must_be_positive_and_key_must_be_present()
    {
        Assert.Throws<DomainException>(() => Payment.Initiate(Guid.NewGuid(), "k", "h", "e2e", new Money(0, "EUR"), "DE1", "DE2", "sepa", Now));
        Assert.Throws<DomainException>(() => Payment.Initiate(Guid.NewGuid(), "", "h", "e2e", new Money(10, "EUR"), "DE1", "DE2", "sepa", Now));
    }

    private static Payment NewPayment() =>
        Payment.Initiate(Guid.NewGuid(), "key-1", "hash-1", "E2E-1", new Money(100.00m, "EUR"), "DE89370400440532013000", "GB33BUKB20201555555555", "sepa", Now);
}
