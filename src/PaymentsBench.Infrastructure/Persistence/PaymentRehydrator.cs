using PaymentsBench.Domain;
using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Infrastructure.Persistence;

/// <summary>The stored shape of a payment, free of any store.</summary>
internal sealed record PaymentState(
    Guid Id,
    string IdempotencyKey,
    string RequestHash,
    string EndToEndId,
    Money Amount,
    string DebtorAccount,
    string CreditorAccount,
    string Scheme,
    PaymentStatus Status,
    string? ReasonCode,
    string? PartnerReference,
    DateOnly? ValueDate,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static PaymentState Of(Payment payment) => new(
        payment.Id, payment.IdempotencyKey, payment.RequestHash, payment.EndToEndId, payment.Amount,
        payment.DebtorAccount, payment.CreditorAccount, payment.Scheme, payment.Status, payment.ReasonCode,
        payment.PartnerReference, payment.ValueDate, payment.CreatedAt, payment.UpdatedAt);
}

/// <summary>
/// Rebuilds the aggregate by replaying the moves the stored status implies. The domain keeps
/// its invariants; persistence does not get a back door. Replay produces events, which are
/// cleared because they were already published when they first happened. Shared by the EF Core
/// and in-memory repositories so both hand out the same kind of detached copy.
/// </summary>
internal static class PaymentRehydrator
{
    public static Payment Rehydrate(PaymentState state)
    {
        var payment = Payment.Initiate(
            state.Id,
            state.IdempotencyKey,
            state.RequestHash,
            state.EndToEndId,
            state.Amount,
            state.DebtorAccount,
            state.CreditorAccount,
            state.Scheme,
            state.CreatedAt);

        var status = state.Status;
        var at = state.UpdatedAt;
        if (status >= PaymentStatus.Submitted)
        {
            payment.MarkSubmitted(at);
        }

        switch (status)
        {
            case PaymentStatus.Accepted:
                payment.MarkAccepted(state.PartnerReference ?? state.EndToEndId, at);
                break;
            case PaymentStatus.Settled:
                payment.MarkAccepted(state.PartnerReference ?? state.EndToEndId, at);
                payment.MarkSettled(state.ValueDate ?? DateOnly.FromDateTime(at.UtcDateTime), at);
                break;
            case PaymentStatus.Rejected:
                payment.MarkRejected(state.ReasonCode ?? "unknown", at);
                break;
            case PaymentStatus.Returned:
                payment.MarkAccepted(state.PartnerReference ?? state.EndToEndId, at);
                payment.MarkSettled(state.ValueDate ?? DateOnly.FromDateTime(at.UtcDateTime), at);
                payment.MarkReturned(state.ReasonCode ?? "unknown", at);
                break;
            case PaymentStatus.Initiated:
            case PaymentStatus.Submitted:
                break;
            default:
                throw new InvalidOperationException($"Unknown stored status {(int)status}.");
        }

        payment.ClearEvents();
        return payment;
    }
}
