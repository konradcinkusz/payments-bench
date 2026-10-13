namespace PaymentsBench.Domain.Payments;

public sealed class InvalidTransitionException : DomainException
{
    public InvalidTransitionException(Guid paymentId, PaymentStatus from, PaymentStatus to)
        : base($"Payment {paymentId} cannot move from {from} to {to}.")
    {
        PaymentId = paymentId;
        From = from;
        To = to;
    }

    public InvalidTransitionException()
    {
    }

    public InvalidTransitionException(string message)
        : base(message)
    {
    }

    public InvalidTransitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public Guid PaymentId { get; }

    public PaymentStatus From { get; }

    public PaymentStatus To { get; }
}
