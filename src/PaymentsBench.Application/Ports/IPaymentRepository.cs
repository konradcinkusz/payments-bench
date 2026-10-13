using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Application.Ports;

public interface IPaymentRepository
{
    Task<Payment?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Payment?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    Task<Payment?> FindByEndToEndIdAsync(string endToEndId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Payment>> ListByStatusAsync(PaymentStatus status, CancellationToken cancellationToken);

    /// <summary>
    /// The ids of Submitted payments last updated before <paramref name="cutoff"/>, oldest first
    /// (ties by id), at most <paramref name="limit"/> of them, plus the oldest <c>UpdatedAt</c> among
    /// all Submitted payments (null when there are none). For the sweep, which must not load aggregates it will not touch.
    /// </summary>
    Task<SubmittedPaymentWindow> ListSubmittedBeforeAsync(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken);

    /// <summary>Filters, then returns the requested page, newest first, ties broken by id, with totals over the filtered set.</summary>
    Task<PaymentPage> ListAsync(PaymentQuery query, CancellationToken cancellationToken);

    Task AddAsync(Payment payment, CancellationToken cancellationToken);

    Task UpdateAsync(Payment payment, CancellationToken cancellationToken);
}

public sealed record SubmittedPaymentWindow(IReadOnlyList<Guid> Ids, DateTimeOffset? OldestUpdatedAt);
