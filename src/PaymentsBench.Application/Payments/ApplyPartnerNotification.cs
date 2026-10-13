using PaymentsBench.Application.Ports;
using PaymentsBench.Domain.Ledger;
using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Application.Payments;

public enum NotificationKind
{
    Settled = 0,
    Rejected = 1,
    Returned = 2,
}

public sealed record PartnerNotification(
    string NotificationId,
    string EndToEndId,
    NotificationKind Kind,
    string? ReasonCode,
    DateOnly? ValueDate);

public enum NotificationOutcome
{
    Applied = 0,
    Duplicate = 1,
    UnknownPayment = 2,
}

/// <summary>
/// Applies what the partner tells us after acceptance. Delivery is at least once, so the inbox
/// is consulted first; a notification for a payment we do not know is recorded and reported,
/// not dropped, because it is exactly what reconciliation needs to see.
/// </summary>
public sealed class ApplyPartnerNotificationHandler(
    IPaymentRepository payments,
    IInbox inbox,
    ILedger ledger,
    IOutbox outbox,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<NotificationOutcome> HandleAsync(PartnerNotification notification, CancellationToken cancellationToken)
    {
        if (!await inbox.TryRecordAsync(notification.NotificationId, cancellationToken))
        {
            return NotificationOutcome.Duplicate;
        }

        var payment = await payments.FindByEndToEndIdAsync(notification.EndToEndId, cancellationToken);
        if (payment is null)
        {
            await unitOfWork.CommitAsync(cancellationToken);
            return NotificationOutcome.UnknownPayment;
        }

        var now = clock.UtcNow;
        switch (notification.Kind)
        {
            case NotificationKind.Settled:
                payment.MarkSettled(notification.ValueDate ?? DateOnly.FromDateTime(now.UtcDateTime), now);
                await ledger.PostAsync(Postings.Settle(payment, now), cancellationToken);
                break;
            case NotificationKind.Rejected:
                payment.MarkRejected(notification.ReasonCode ?? "partner-rejected", now);
                await ledger.PostAsync(Postings.Release(payment, now), cancellationToken);
                break;
            case NotificationKind.Returned:
                payment.MarkReturned(notification.ReasonCode ?? "returned", now);
                await ledger.PostAsync(Postings.Reverse(payment, now), cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"Unhandled notification kind {notification.Kind}.");
        }

        await payments.UpdateAsync(payment, cancellationToken);
        foreach (var paymentEvent in payment.UncommittedEvents)
        {
            await outbox.EnqueueAsync(paymentEvent, cancellationToken);
        }

        payment.ClearEvents();
        await unitOfWork.CommitAsync(cancellationToken);
        return NotificationOutcome.Applied;
    }
}
