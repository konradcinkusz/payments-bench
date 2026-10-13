using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PaymentsBench.Application.Ports;
using PaymentsBench.Domain;
using PaymentsBench.Domain.Ledger;
using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Infrastructure.Persistence;

/// <summary>
/// Optimistic concurrency by remembered version (P4). Every payment handed out is remembered
/// with the <c>Version</c> it was read at, per repository instance, which is per scope. An update
/// checks <em>that</em> version, never a fresh read, so a handler that lost a race fails at commit
/// instead of overwriting the winner. Once a commit succeeds the remembered version advances,
/// which keeps a handler that updates the same payment twice in one scope working.
/// </summary>
public sealed class EfPaymentRepository : IPaymentRepository
{
    private readonly PaymentsDbContext db;
    private readonly Dictionary<Guid, int> readVersions = [];
    private readonly Dictionary<Guid, int> pendingVersions = [];

    public EfPaymentRepository(PaymentsDbContext db)
    {
        this.db = db;
        db.SavedChanges += (_, _) => AdvanceCommittedVersions();
    }

    public async Task<Payment?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Hand(await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken));

    public async Task<Payment?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        Hand(await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.IdempotencyKey == idempotencyKey, cancellationToken));

    public async Task<Payment?> FindByEndToEndIdAsync(string endToEndId, CancellationToken cancellationToken) =>
        Hand(await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.EndToEndId == endToEndId, cancellationToken));

    public async Task<IReadOnlyList<Payment>> ListByStatusAsync(PaymentStatus status, CancellationToken cancellationToken)
    {
        var rows = await db.Payments.AsNoTracking().Where(p => p.Status == (int)status).ToListAsync(cancellationToken);
        return rows.Select(r => Hand(r)!).ToList();
    }

    public async Task<PaymentPage> ListAsync(PaymentQuery query, CancellationToken cancellationToken)
    {
        var filtered = db.Payments.AsNoTracking().AsQueryable();
        if (query.Status is { } status)
        {
            filtered = filtered.Where(p => p.Status == (int)status);
        }

        if (query.Scheme is { } scheme)
        {
            filtered = filtered.Where(p => p.Scheme == scheme);
        }

        if (query.EndToEndId is { } endToEndId)
        {
            filtered = filtered.Where(p => p.EndToEndId == endToEndId);
        }

        if (query.CreatedFrom is { } createdFrom)
        {
            filtered = filtered.Where(p => p.CreatedAt >= createdFrom);
        }

        if (query.CreatedTo is { } createdTo)
        {
            filtered = filtered.Where(p => p.CreatedAt <= createdTo);
        }

        // One grouped query over the filtered base gives every per-status count; the total is their sum.
        var grouped = await filtered
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var counts = grouped.ToDictionary(g => (PaymentStatus)g.Status, g => g.Count);

        var rows = await filtered
            .OrderByDescending(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .Skip(query.Skip)
            .Take(query.Limit)
            .ToListAsync(cancellationToken);

        return new PaymentPage(rows.Select(r => Hand(r)!).ToList(), counts.Values.Sum(), counts);
    }

    public Task AddAsync(Payment payment, CancellationToken cancellationToken)
    {
        db.Payments.Add(ToRow(payment, version: 0));
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Payment payment, CancellationToken cancellationToken)
    {
        if (!readVersions.TryGetValue(payment.Id, out var readVersion))
        {
            throw new InvalidOperationException(
                $"Payment {payment.Id} was not read in this scope, so there is no version to check. Load it with GetAsync, FindBy* or a list before updating it.");
        }

        var next = ToRow(payment, readVersion + 1);
        var tracked = db.ChangeTracker.Entries<PaymentRow>().FirstOrDefault(e => e.Entity.Id == payment.Id);
        if (tracked is null)
        {
            db.Payments.Attach(next);
            tracked = db.Entry(next);
            tracked.State = EntityState.Modified;
        }
        else
        {
            tracked.CurrentValues.SetValues(next);
        }

        // The check is against the version the aggregate was read at, not whatever is stored now.
        tracked.Property(p => p.Version).OriginalValue = readVersion;
        pendingVersions[payment.Id] = readVersion + 1;
        return Task.CompletedTask;
    }

    private void AdvanceCommittedVersions()
    {
        foreach (var (id, version) in pendingVersions)
        {
            readVersions[id] = version;
        }

        pendingVersions.Clear();
    }

    private Payment? Hand(PaymentRow? row)
    {
        if (row is null)
        {
            return null;
        }

        readVersions[row.Id] = row.Version;
        return PaymentRehydrator.Rehydrate(new PaymentState(
            row.Id, row.IdempotencyKey, row.RequestHash, row.EndToEndId, new Money(row.Amount, row.Currency),
            row.DebtorAccount, row.CreditorAccount, row.Scheme, (PaymentStatus)row.Status, row.ReasonCode,
            row.PartnerReference, row.ValueDate, row.CreatedAt, row.UpdatedAt));
    }

    private static PaymentRow ToRow(Payment payment, int version) => new()
    {
        Id = payment.Id,
        IdempotencyKey = payment.IdempotencyKey,
        RequestHash = payment.RequestHash,
        EndToEndId = payment.EndToEndId,
        Amount = payment.Amount.Amount,
        Currency = payment.Amount.Currency,
        DebtorAccount = payment.DebtorAccount,
        CreditorAccount = payment.CreditorAccount,
        Scheme = payment.Scheme,
        Status = (int)payment.Status,
        ReasonCode = payment.ReasonCode,
        PartnerReference = payment.PartnerReference,
        ValueDate = payment.ValueDate,
        CreatedAt = payment.CreatedAt,
        UpdatedAt = payment.UpdatedAt,
        Version = version,
    };
}

public sealed class EfLedger(PaymentsDbContext db) : ILedger
{
    public Task PostAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        var row = new JournalEntryRow
        {
            Id = entry.Id,
            PaymentId = entry.PaymentId,
            Description = entry.Description,
            PostedAt = entry.PostedAt,
        };
        row.Lines.AddRange(entry.Lines.Select(l => new JournalLineRow
        {
            JournalEntryId = entry.Id,
            Account = l.Account,
            Side = (int)l.Side,
            Amount = l.Amount.Amount,
            Currency = l.Amount.Currency,
        }));
        db.JournalEntries.Add(row);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<JournalEntry>> EntriesForAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var rows = await db.JournalEntries.AsNoTracking().Include(e => e.Lines)
            .Where(e => e.PaymentId == paymentId).OrderBy(e => e.PostedAt).ToListAsync(cancellationToken);
        return rows.Select(r => new JournalEntry(
            r.Id,
            r.PaymentId,
            r.Description,
            r.PostedAt,
            r.Lines.Select(l => new JournalLine(l.Account, (Side)l.Side, new Money(l.Amount, l.Currency))).ToList())).ToList();
    }

    public async Task<decimal> BalanceAsync(string account, string currency, CancellationToken cancellationToken)
    {
        var lines = db.JournalLines.AsNoTracking().Where(l => l.Account == account && l.Currency == currency);
        var debits = await lines.Where(l => l.Side == (int)Side.Debit).SumAsync(l => l.Amount, cancellationToken);
        var credits = await lines.Where(l => l.Side == (int)Side.Credit).SumAsync(l => l.Amount, cancellationToken);
        return debits - credits;
    }
}

public sealed class EfOutbox(PaymentsDbContext db) : IOutbox
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public Task EnqueueAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken)
    {
        db.OutboxMessages.Add(new OutboxMessageRow
        {
            Id = Guid.NewGuid(),
            Type = paymentEvent.GetType().Name,
            Payload = JsonSerializer.Serialize(paymentEvent, paymentEvent.GetType(), SerializerOptions),
            OccurredAt = paymentEvent.OccurredAt,
        });
        return Task.CompletedTask;
    }
}

public sealed class EfInbox(PaymentsDbContext db) : IInbox
{
    public async Task<bool> TryRecordAsync(string notificationId, CancellationToken cancellationToken)
    {
        if (await db.InboxMessages.AnyAsync(m => m.NotificationId == notificationId, cancellationToken))
        {
            return false;
        }

        db.InboxMessages.Add(new InboxMessageRow { NotificationId = notificationId, ReceivedAt = DateTimeOffset.UtcNow });
        return true;
    }
}

public sealed class EfUnitOfWork(PaymentsDbContext db) : IUnitOfWork
{
    private const string IdempotencyKeyIndex = "IX_payments_IdempotencyKey";
    private const string EndToEndIdIndex = "IX_payments_EndToEndId";

    /// <summary>
    /// Saves everything staged in the scope or nothing. A lost optimistic-concurrency race, or a
    /// unique violation on the payment's idempotency key or end-to-end id (a concurrent duplicate
    /// initiation), becomes <see cref="ConcurrencyConflictException"/>, and the change tracker is
    /// cleared so the scope is not left half-applied. No EF or Npgsql type leaves this class (P11).
    /// </summary>
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException e)
        {
            var id = e.Entries.Select(x => x.Entity).OfType<PaymentRow>().Select(r => (Guid?)r.Id).FirstOrDefault();
            db.ChangeTracker.Clear();
            throw id is { } paymentId ? ConcurrencyConflictException.Changed(paymentId, e) : new ConcurrencyConflictException(
                "The payment changed concurrently. Re-read it and try again.", e);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
            && pg.ConstraintName is IdempotencyKeyIndex or EndToEndIdIndex)
        {
            db.ChangeTracker.Clear();
            throw ConcurrencyConflictException.Duplicate(e);
        }
    }
}
