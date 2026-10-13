using PaymentsBench.Application.Ports;
using PaymentsBench.Domain.Ledger;
using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Application.Payments;

public sealed record SubmitPayment(Guid PaymentId);

/// <summary>
/// Sends one initiated payment to the partner. The status moves to Submitted and is committed
/// <em>before</em> the call, so a crash mid-call leaves evidence that a transfer may exist on
/// the partner side. An unknown outcome is left at Submitted for <see cref="ResolveSubmittedPaymentHandler"/>;
/// this handler never retries a submit.
/// </summary>
public sealed class SubmitPaymentHandler(
    IPaymentRepository payments,
    IPartnerGateway partner,
    ILedger ledger,
    IOutbox outbox,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<Payment> HandleAsync(SubmitPayment command, CancellationToken cancellationToken)
    {
        var payment = await payments.GetAsync(command.PaymentId, cancellationToken)
            ?? throw new PaymentNotFoundException(command.PaymentId);

        payment.MarkSubmitted(clock.UtcNow);
        await payments.UpdateAsync(payment, cancellationToken);
        await PublishAsync(payment, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        var result = await partner.SubmitAsync(ToTransfer(payment), cancellationToken);
        var now = clock.UtcNow;
        switch (result.Outcome)
        {
            case SubmitOutcome.Accepted:
                payment.MarkAccepted(result.PartnerReference!, now);
                break;
            case SubmitOutcome.Rejected:
                payment.MarkRejected(result.ReasonCode ?? "partner-rejected", now);
                await ledger.PostAsync(Postings.Release(payment, now), cancellationToken);
                break;
            case SubmitOutcome.Unknown:
                // Deliberately nothing: the payment stays Submitted and the resolver asks the partner.
                return payment;
            default:
                throw new InvalidOperationException($"Unhandled submit outcome {result.Outcome}.");
        }

        await payments.UpdateAsync(payment, cancellationToken);
        await PublishAsync(payment, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return payment;
    }

    internal static PartnerTransfer ToTransfer(Payment payment) => new(
        payment.EndToEndId,
        payment.Amount.Amount,
        payment.Amount.Currency,
        payment.DebtorAccount,
        payment.CreditorAccount,
        payment.Scheme);

    private async Task PublishAsync(Payment payment, CancellationToken cancellationToken)
    {
        foreach (var paymentEvent in payment.UncommittedEvents)
        {
            await outbox.EnqueueAsync(paymentEvent, cancellationToken);
        }

        payment.ClearEvents();
    }
}

public sealed class PaymentNotFoundException : Domain.DomainException
{
    public PaymentNotFoundException(Guid paymentId)
        : base($"Payment {paymentId} does not exist.")
    {
    }

    public PaymentNotFoundException()
    {
    }

    public PaymentNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
