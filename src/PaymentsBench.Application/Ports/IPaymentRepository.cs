using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Application.Ports;

public interface IPaymentRepository
{
    Task<Payment?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Payment?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    Task<Payment?> FindByEndToEndIdAsync(string endToEndId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Payment>> ListByStatusAsync(PaymentStatus status, CancellationToken cancellationToken);

    /// <summary>Filters, then returns the requested page, newest first, ties broken by id, with totals over the filtered set.</summary>
    Task<PaymentPage> ListAsync(PaymentQuery query, CancellationToken cancellationToken);

    Task AddAsync(Payment payment, CancellationToken cancellationToken);

    Task UpdateAsync(Payment payment, CancellationToken cancellationToken);
}
