using PaymentsBench.Domain;

namespace PaymentsBench.Application.Ports;

/// <summary>
/// Two writers raced and this one lost: the payment was changed after it was read, or another
/// request created the same payment first. Nothing of the losing scope was applied. The caller
/// should re-read and decide again; a blind retry of a write that was based on stale state is
/// exactly what the check exists to stop. Raised by the stores so no persistence type crosses
/// into Application or Api.
/// </summary>
public sealed class ConcurrencyConflictException : DomainException
{
    public ConcurrencyConflictException(string message)
        : base(message)
    {
    }

    public ConcurrencyConflictException()
        : base("The payment changed concurrently. Re-read it and try again.")
    {
    }

    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public static ConcurrencyConflictException Changed(Guid paymentId, Exception? inner = null) =>
        new($"Payment {paymentId} changed concurrently. Re-read it and try again.", inner!);

    public static ConcurrencyConflictException Duplicate(Exception? inner = null) =>
        new("A payment with this idempotency key or end-to-end id was created concurrently. Re-read it instead of creating it again.", inner!);
}
