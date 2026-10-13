using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using PaymentsBench.Application.Ports;
using PaymentsBench.Domain.Ledger;
using PaymentsBench.Domain.Payments;
using PaymentsBench.Infrastructure.Persistence;

namespace PaymentsBench.Infrastructure.InMemory;

/// <summary>
/// The degraded mode: everything in process, nothing durable. The API runs with it when no
/// connection string is configured, and the tests run on it. The same ports, so a handler
/// cannot tell the difference, which is the point.
/// </summary>
public sealed class InMemoryPaymentRepository : IPaymentRepository
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Stored> payments = [];

    // The version each handed-out instance was read at. Every read returns a fresh copy, so two
    // readers of one payment hold two instances with their own remembered version, as two scopes do on PostgreSQL.
    private readonly ConditionalWeakTable<Payment, VersionBox> readVersions = [];

    public Task<Payment?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(payments.TryGetValue(id, out var stored) ? Hand(stored) : null);
        }
    }

    public Task<Payment?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(HandFirst(s => s.State.IdempotencyKey == idempotencyKey));
        }
    }

    public Task<Payment?> FindByEndToEndIdAsync(string endToEndId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(HandFirst(s => s.State.EndToEndId == endToEndId));
        }
    }

    public Task<IReadOnlyList<Payment>> ListByStatusAsync(PaymentStatus status, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<Payment>>(payments.Values.Where(s => s.State.Status == status).Select(Hand).ToList());
        }
    }

    public Task<SubmittedPaymentWindow> ListSubmittedBeforeAsync(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var submitted = payments.Values.Select(s => s.State).Where(p => p.Status == PaymentStatus.Submitted).ToList();
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
    }

    public Task<PaymentPage> ListAsync(PaymentQuery query, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var filtered = payments.Values.Where(s => Matches(s.State, query)).ToList();

            var counts = filtered.GroupBy(s => s.State.Status).ToDictionary(g => g.Key, g => g.Count());
            var items = filtered
                .OrderByDescending(s => s.State.CreatedAt).ThenBy(s => s.State.Id)
                .Skip(query.Skip).Take(query.Limit)
                .Select(Hand)
                .ToList();
            return Task.FromResult(new PaymentPage(items, filtered.Count, counts));
        }
    }

    private static bool Matches(PaymentState payment, PaymentQuery query)
    {
        if (query.Status is not null && payment.Status != query.Status)
        {
            return false;
        }

        if (query.Scheme is not null && payment.Scheme != query.Scheme)
        {
            return false;
        }

        if (query.EndToEndId is not null && payment.EndToEndId != query.EndToEndId)
        {
            return false;
        }

        return (query.CreatedFrom is null || payment.CreatedAt >= query.CreatedFrom)
            && (query.CreatedTo is null || payment.CreatedAt <= query.CreatedTo);
    }

    public Task AddAsync(Payment payment, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (payments.ContainsKey(payment.Id))
            {
                throw new InvalidOperationException($"Payment {payment.Id} already exists.");
            }

            // The unique indexes PostgreSQL enforces on the idempotency key and the end-to-end id.
            if (payments.Values.Any(s => s.State.IdempotencyKey == payment.IdempotencyKey || s.State.EndToEndId == payment.EndToEndId))
            {
                throw ConcurrencyConflictException.Duplicate();
            }

            payments[payment.Id] = new Stored(PaymentState.Of(payment), 0);
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(Payment payment, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!readVersions.TryGetValue(payment, out var read))
            {
                throw new InvalidOperationException(
                    $"Payment {payment.Id} was not read from this repository, so there is no version to check. Load it with GetAsync, FindBy* or a list before updating it.");
            }

            if (!payments.TryGetValue(payment.Id, out var stored) || stored.Version != read.Value)
            {
                throw ConcurrencyConflictException.Changed(payment.Id);
            }

            payments[payment.Id] = new Stored(PaymentState.Of(payment), stored.Version + 1);
            read.Value = stored.Version + 1;
        }

        return Task.CompletedTask;
    }

    private Payment? HandFirst(Func<Stored, bool> predicate) =>
        payments.Values.FirstOrDefault(predicate) is { } stored ? Hand(stored) : null;

    private Payment Hand(Stored stored)
    {
        var copy = PaymentRehydrator.Rehydrate(stored.State);
        readVersions.Add(copy, new VersionBox(stored.Version));
        return copy;
    }

    private sealed record Stored(PaymentState State, int Version);

    private sealed class VersionBox(int value)
    {
        public int Value { get; set; } = value;
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
