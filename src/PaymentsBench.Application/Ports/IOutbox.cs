using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Application.Ports;

/// <summary>
/// Events leave the service through here and only here, written in the same transaction as the
/// state change that caused them. A dispatcher reads them later; nothing publishes inline.
/// </summary>
public interface IOutbox
{
    Task EnqueueAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken);
}
