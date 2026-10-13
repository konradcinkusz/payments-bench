namespace PaymentsBench.Domain.Payments;

/// <summary>
/// An outbound bank transfer. The aggregate owns its status, its reason code and its partner
/// reference; it records domain events for the outbox and refuses any move the transition
/// table does not list. It knows nothing about HTTP, partners or databases.
/// </summary>
public sealed class Payment
{
    private readonly List<PaymentEvent> _events = [];

    private Payment(
        Guid id,
        string idempotencyKey,
        string requestHash,
        string endToEndId,
        Money amount,
        string debtorAccount,
        string creditorAccount,
        string scheme,
        DateTimeOffset createdAt)
    {
        Id = id;
        IdempotencyKey = idempotencyKey;
        RequestHash = requestHash;
        EndToEndId = endToEndId;
        Amount = amount;
        DebtorAccount = debtorAccount;
        CreditorAccount = creditorAccount;
        Scheme = scheme;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Status = PaymentStatus.Initiated;
    }

    public Guid Id { get; }

    /// <summary>The caller's key. Two requests with the same key are the same request.</summary>
    public string IdempotencyKey { get; }

    /// <summary>A hash of the request body, so a replay with a different body is a conflict, not a replay.</summary>
    public string RequestHash { get; }

    /// <summary>The reference that travels to the partner and comes back on statements.</summary>
    public string EndToEndId { get; }

    public Money Amount { get; }

    public string DebtorAccount { get; }

    public string CreditorAccount { get; }

    public string Scheme { get; }

    public PaymentStatus Status { get; private set; }

    public string? ReasonCode { get; private set; }

    public string? PartnerReference { get; private set; }

    public DateOnly? ValueDate { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<PaymentEvent> UncommittedEvents => _events;

    public static Payment Initiate(
        Guid id,
        string idempotencyKey,
        string requestHash,
        string endToEndId,
        Money amount,
        string debtorAccount,
        string creditorAccount,
        string scheme,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new DomainException("An idempotency key is required.");
        }

        if (!amount.IsPositive)
        {
            throw new DomainException("A payment amount must be positive.");
        }

        if (string.IsNullOrWhiteSpace(endToEndId))
        {
            throw new DomainException("An end-to-end id is required.");
        }

        var payment = new Payment(id, idempotencyKey, requestHash, endToEndId, amount, debtorAccount, creditorAccount, scheme, now);
        payment._events.Add(new PaymentInitiated(id, now, amount, scheme));
        return payment;
    }

    /// <summary>Called before the partner call is made, so a crash mid-call leaves a record that something may have been sent.</summary>
    public void MarkSubmitted(DateTimeOffset now)
    {
        Move(PaymentStatus.Submitted, now);
        _events.Add(new PaymentSubmitted(Id, now));
    }

    public void MarkAccepted(string partnerReference, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(partnerReference))
        {
            throw new DomainException("The partner reference is required on acceptance.");
        }

        Move(PaymentStatus.Accepted, now);
        PartnerReference = partnerReference;
        _events.Add(new PaymentAccepted(Id, now, partnerReference));
    }

    public void MarkSettled(DateOnly valueDate, DateTimeOffset now)
    {
        Move(PaymentStatus.Settled, now);
        ValueDate = valueDate;
        _events.Add(new PaymentSettled(Id, now, valueDate));
    }

    public void MarkRejected(string reasonCode, DateTimeOffset now)
    {
        RequireReason(reasonCode);
        Move(PaymentStatus.Rejected, now);
        ReasonCode = reasonCode;
        _events.Add(new PaymentRejected(Id, now, reasonCode));
    }

    public void MarkReturned(string reasonCode, DateTimeOffset now)
    {
        RequireReason(reasonCode);
        Move(PaymentStatus.Returned, now);
        ReasonCode = reasonCode;
        _events.Add(new PaymentReturned(Id, now, reasonCode));
    }

    public void ClearEvents() => _events.Clear();

    private static void RequireReason(string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            throw new DomainException("A reason code is required on every failure.");
        }
    }

    private void Move(PaymentStatus to, DateTimeOffset now)
    {
        if (!PaymentTransitions.CanMove(Status, to))
        {
            throw new InvalidTransitionException(Id, Status, to);
        }

        Status = to;
        UpdatedAt = now;
    }
}
