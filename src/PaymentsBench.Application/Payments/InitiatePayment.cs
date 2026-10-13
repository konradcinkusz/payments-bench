using PaymentsBench.Application.Ports;
using PaymentsBench.Domain;
using PaymentsBench.Domain.Ledger;
using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Application.Payments;

public sealed record InitiatePayment(
    string IdempotencyKey,
    string EndToEndId,
    decimal Amount,
    string Currency,
    string DebtorAccount,
    string CreditorAccount,
    string Scheme);

public sealed class IdempotencyConflictException : DomainException
{
    public IdempotencyConflictException(string idempotencyKey)
        : base($"Idempotency key '{idempotencyKey}' was already used with a different request.")
    {
    }

    public IdempotencyConflictException()
    {
    }

    public IdempotencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Creates a payment once per idempotency key. A replay with the same body returns the original
/// payment; a replay with a different body is refused. Reserving the funds in the ledger and
/// enqueuing the event happen in the same unit of work as the insert.
/// </summary>
public sealed class InitiatePaymentHandler(
    IPaymentRepository payments,
    ILedger ledger,
    IOutbox outbox,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<Payment> HandleAsync(InitiatePayment command, CancellationToken cancellationToken)
    {
        var hash = RequestHash.Of(command);
        var existing = await payments.FindByIdempotencyKeyAsync(command.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return existing.RequestHash == hash
                ? existing
                : throw new IdempotencyConflictException(command.IdempotencyKey);
        }

        var now = clock.UtcNow;
        var payment = Payment.Initiate(
            Guid.NewGuid(),
            command.IdempotencyKey,
            hash,
            command.EndToEndId,
            new Money(command.Amount, command.Currency),
            command.DebtorAccount,
            command.CreditorAccount,
            command.Scheme,
            now);

        await payments.AddAsync(payment, cancellationToken);
        await ledger.PostAsync(Postings.Reserve(payment, now), cancellationToken);
        await PublishAsync(payment, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return payment;
    }

    private async Task PublishAsync(Payment payment, CancellationToken cancellationToken)
    {
        foreach (var paymentEvent in payment.UncommittedEvents)
        {
            await outbox.EnqueueAsync(paymentEvent, cancellationToken);
        }

        payment.ClearEvents();
    }
}
