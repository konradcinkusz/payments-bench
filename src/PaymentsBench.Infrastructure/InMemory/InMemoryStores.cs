using System.Collections.Concurrent;
using PaymentsBench.Application.Ports;
using PaymentsBench.Domain.Ledger;
using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Infrastructure.InMemory;

/// <summary>
/// The degraded mode: everything in process, nothing durable. The API runs with it when no
/// connection string is configured, and the tests run on it. The same ports, so a handler
/// cannot tell the difference, which is the point.
/// </summary>
public sealed class InMemoryPaymentRepository : IPaymentRepository
{
    private readonly ConcurrentDictionary<Guid, Payment> _payments = new();

    public Task<Payment?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_payments.GetValueOrDefault(id));

    public Task<Payment?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        Task.FromResult(_payments.Values.FirstOrDefault(p => p.IdempotencyKey == idempotencyKey));

    public Task<Payment?> FindByEndToEndIdAsync(string endToEndId, CancellationToken cancellationToken) =>
        Task.FromResult(_payments.Values.FirstOrDefault(p => p.EndToEndId == endToEndId));

    public Task<IReadOnlyList<Payment>> ListByStatusAsync(PaymentStatus status, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Payment>>(_payments.Values.Where(p => p.Status == status).ToList());

    public Task<SubmittedPaymentWindow> ListSubmittedBeforeAsync(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken)
    {
        var submitted = _payments.Values.Where(p => p.Status == PaymentStatus.Submitted).ToList();
        var ids = submitted
            .Where(p => p.UpdatedAt < cutoff)
            .OrderBy(p => p.UpdatedAt)
            .ThenBy(p => p.Id)
            .Take(limit)
            .Select(p => p.Id)
            .ToList();
        DateTimeOffset? oldest = submitted.Count == 0 ? null : submitted.Min(p => p.UpdatedAt);
        return Task.FromResult(new SubmittedPaymentWindow(ids, oldest));
    }

    public Task AddAsync(Payment payment, CancellationToken cancellationToken)
    {
        if (!_payments.TryAdd(payment.Id, payment))
        {
            throw new InvalidOperationException($"Payment {payment.Id} already exists.");
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(Payment payment, CancellationToken cancellationToken)
    {
        _payments[payment.Id] = payment;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryLedger : ILedger
{
    private readonly ConcurrentBag<JournalEntry> _entries = [];

    public IReadOnlyCollection<JournalEntry> Entries => _entries;

    public Task PostAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JournalEntry>> EntriesForAsync(Guid paymentId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<JournalEntry>>(_entries.Where(e => e.PaymentId == paymentId).OrderBy(e => e.PostedAt).ToList());

    public Task<decimal> BalanceAsync(string account, string currency, CancellationToken cancellationToken)
    {
        var lines = _entries.SelectMany(e => e.Lines).Where(l => l.Account == account && l.Amount.Currency == currency);
        var balance = lines.Sum(l => l.Side == Side.Debit ? l.Amount.Amount : -l.Amount.Amount);
        return Task.FromResult(balance);
    }
}

public sealed class InMemoryOutbox : IOutbox
{
    private readonly ConcurrentQueue<PaymentEvent> _events = new();

    public IReadOnlyCollection<PaymentEvent> Events => _events;

    public Task EnqueueAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken)
    {
        _events.Enqueue(paymentEvent);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryInbox : IInbox
{
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

    public Task<bool> TryRecordAsync(string notificationId, CancellationToken cancellationToken) =>
        Task.FromResult(_seen.TryAdd(notificationId, 0));
}

public sealed class NoOpUnitOfWork : IUnitOfWork
{
    public int Commits { get; private set; }

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        Commits++;
        return Task.CompletedTask;
    }
}
