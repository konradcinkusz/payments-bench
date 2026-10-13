using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PaymentsBench.Application.Ports;
using PaymentsBench.Domain;
using PaymentsBench.Domain.Ledger;
using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Infrastructure.Persistence;

public sealed class EfPaymentRepository(PaymentsDbContext db) : IPaymentRepository
{
    public async Task<Payment?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Rehydrate(await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken));

    public async Task<Payment?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        Rehydrate(await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.IdempotencyKey == idempotencyKey, cancellationToken));

    public async Task<Payment?> FindByEndToEndIdAsync(string endToEndId, CancellationToken cancellationToken) =>
        Rehydrate(await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.EndToEndId == endToEndId, cancellationToken));

    public async Task<IReadOnlyList<Payment>> ListByStatusAsync(PaymentStatus status, CancellationToken cancellationToken)
    {
        var rows = await db.Payments.AsNoTracking().Where(p => p.Status == (int)status).ToListAsync(cancellationToken);
        return rows.Select(r => Rehydrate(r)!).ToList();
    }

    public async Task<SubmittedPaymentWindow> ListSubmittedBeforeAsync(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken)
    {
        // Both queries filter on Status first, so the Status index narrows to the Submitted rows; the cut-off, order and limit run in SQL.
        var submitted = (int)PaymentStatus.Submitted;
        var ids = await db.Payments.AsNoTracking()
            .Where(p => p.Status == submitted && p.UpdatedAt < cutoff)
            .OrderBy(p => p.UpdatedAt)
            .ThenBy(p => p.Id)
            .Take(limit)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
        var oldest = await db.Payments.AsNoTracking()
            .Where(p => p.Status == submitted)
            .MinAsync(p => (DateTimeOffset?)p.UpdatedAt, cancellationToken);
        return new SubmittedPaymentWindow(ids, oldest);
    }

    public Task AddAsync(Payment payment, CancellationToken cancellationToken)
    {
        db.Payments.Add(ToRow(payment, version: 0));
        return Task.CompletedTask;
    }

    public async Task UpdateAsync(Payment payment, CancellationToken cancellationToken)
    {
        var row = await db.Payments.SingleAsync(p => p.Id == payment.Id, cancellationToken);
        var next = ToRow(payment, row.Version + 1);
        db.Entry(row).CurrentValues.SetValues(next);
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

    /// <summary>
    /// Rebuilds the aggregate by replaying the moves the stored status implies. The domain keeps
    /// its invariants; persistence does not get a back door. Replay produces events, which are
    /// cleared because they were already published when they first happened.
    /// </summary>
    private static Payment? Rehydrate(PaymentRow? row)
    {
        if (row is null)
        {
            return null;
        }

        var payment = Payment.Initiate(
            row.Id,
            row.IdempotencyKey,
            row.RequestHash,
            row.EndToEndId,
            new Money(row.Amount, row.Currency),
            row.DebtorAccount,
            row.CreditorAccount,
            row.Scheme,
            row.CreatedAt);

        var status = (PaymentStatus)row.Status;
        var at = row.UpdatedAt;
        if (status >= PaymentStatus.Submitted)
        {
            payment.MarkSubmitted(at);
        }

        switch (status)
        {
            case PaymentStatus.Accepted:
                payment.MarkAccepted(row.PartnerReference ?? row.EndToEndId, at);
                break;
            case PaymentStatus.Settled:
                payment.MarkAccepted(row.PartnerReference ?? row.EndToEndId, at);
                payment.MarkSettled(row.ValueDate ?? DateOnly.FromDateTime(at.UtcDateTime), at);
                break;
            case PaymentStatus.Rejected:
                payment.MarkRejected(row.ReasonCode ?? "unknown", at);
                break;
            case PaymentStatus.Returned:
                payment.MarkAccepted(row.PartnerReference ?? row.EndToEndId, at);
                payment.MarkSettled(row.ValueDate ?? DateOnly.FromDateTime(at.UtcDateTime), at);
                payment.MarkReturned(row.ReasonCode ?? "unknown", at);
                break;
            case PaymentStatus.Initiated:
            case PaymentStatus.Submitted:
                break;
            default:
                throw new InvalidOperationException($"Unknown stored status {row.Status}.");
        }

        payment.ClearEvents();
        return payment;
    }
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
    public Task CommitAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
