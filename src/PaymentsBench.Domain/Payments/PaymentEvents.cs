namespace PaymentsBench.Domain.Payments;

/// <summary>Something that happened to a payment. Published through the outbox, never directly.</summary>
public abstract record PaymentEvent(Guid PaymentId, DateTimeOffset OccurredAt);

public sealed record PaymentInitiated(Guid PaymentId, DateTimeOffset OccurredAt, Money Amount, string Scheme) : PaymentEvent(PaymentId, OccurredAt);

public sealed record PaymentSubmitted(Guid PaymentId, DateTimeOffset OccurredAt) : PaymentEvent(PaymentId, OccurredAt);

public sealed record PaymentAccepted(Guid PaymentId, DateTimeOffset OccurredAt, string PartnerReference) : PaymentEvent(PaymentId, OccurredAt);

public sealed record PaymentSettled(Guid PaymentId, DateTimeOffset OccurredAt, DateOnly ValueDate) : PaymentEvent(PaymentId, OccurredAt);

public sealed record PaymentRejected(Guid PaymentId, DateTimeOffset OccurredAt, string ReasonCode) : PaymentEvent(PaymentId, OccurredAt);

public sealed record PaymentReturned(Guid PaymentId, DateTimeOffset OccurredAt, string ReasonCode) : PaymentEvent(PaymentId, OccurredAt);
