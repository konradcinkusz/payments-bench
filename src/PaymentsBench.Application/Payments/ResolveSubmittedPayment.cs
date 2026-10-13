using PaymentsBench.Application.Ports;
using PaymentsBench.Domain.Ledger;
using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Application.Payments;

public sealed record ResolveSubmittedPayment(Guid PaymentId);

public enum ResolutionOutcome
{
    AlreadyResolved = 0,
    Accepted = 1,
    Settled = 2,
    Rejected = 3,
    /// <summary>The partner has no record of the transfer, so the submit never landed. It is safe to resubmit.</summary>
    SafeToResubmit = 4,
}

/// <summary>
/// The recovery for an unknown submit outcome. It asks the partner by end-to-end id and applies
/// what the partner says. It resubmits only when the partner reports <see cref="PartnerStatus.NotFound"/>,
/// because that is the only answer that rules out a duplicate transfer.
/// </summary>
public sealed class ResolveSubmittedPaymentHandler(
    IPaymentRepository payments,
    IPartnerGateway partner,
    ILedger ledger,
    IOutbox outbox,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<ResolutionOutcome> HandleAsync(ResolveSubmittedPayment command, CancellationToken cancellationToken)
    {
        var payment = await payments.GetAsync(command.PaymentId, cancellationToken)
            ?? throw new PaymentNotFoundException(command.PaymentId);

        if (payment.Status != PaymentStatus.Submitted)
        {
            return ResolutionOutcome.AlreadyResolved;
        }

        var status = await partner.GetStatusAsync(payment.EndToEndId, cancellationToken);
        var now = clock.UtcNow;
        ResolutionOutcome outcome;
        switch (status.Status)
        {
            case PartnerStatus.NotFound:
                var resubmit = await partner.SubmitAsync(SubmitPaymentHandler.ToTransfer(payment), cancellationToken);
                if (resubmit.Outcome == SubmitOutcome.Accepted)
                {
                    payment.MarkAccepted(resubmit.PartnerReference!, now);
                    outcome = ResolutionOutcome.Accepted;
                }
                else if (resubmit.Outcome == SubmitOutcome.Rejected)
                {
                    payment.MarkRejected(resubmit.ReasonCode ?? "partner-rejected", now);
                    await ledger.PostAsync(Postings.Release(payment, now), cancellationToken);
                    outcome = ResolutionOutcome.Rejected;
                }
                else
                {
                    return ResolutionOutcome.SafeToResubmit;
                }

                break;
            case PartnerStatus.Accepted:
                payment.MarkAccepted(status.PartnerReference ?? payment.EndToEndId, now);
                outcome = ResolutionOutcome.Accepted;
                break;
            case PartnerStatus.Settled:
                payment.MarkSettled(status.ValueDate ?? DateOnly.FromDateTime(now.UtcDateTime), now);
                await ledger.PostAsync(Postings.Settle(payment, now), cancellationToken);
                outcome = ResolutionOutcome.Settled;
                break;
            case PartnerStatus.Rejected:
                payment.MarkRejected(status.ReasonCode ?? "partner-rejected", now);
                await ledger.PostAsync(Postings.Release(payment, now), cancellationToken);
                outcome = ResolutionOutcome.Rejected;
                break;
            case PartnerStatus.Returned:
                throw new InvalidOperationException("A partner cannot report a return for a transfer that was never accepted on our side.");
            default:
                throw new InvalidOperationException($"Unhandled partner status {status.Status}.");
        }

        await payments.UpdateAsync(payment, cancellationToken);
        foreach (var paymentEvent in payment.UncommittedEvents)
        {
            await outbox.EnqueueAsync(paymentEvent, cancellationToken);
        }

        payment.ClearEvents();
        await unitOfWork.CommitAsync(cancellationToken);
        return outcome;
    }
}
