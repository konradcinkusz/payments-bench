using Microsoft.EntityFrameworkCore;

namespace PaymentsBench.Infrastructure.Persistence;

/// <summary>
/// Rows, not aggregates: the aggregate is rebuilt from <see cref="PaymentRow"/> by the repository,
/// so the domain never sees EF Core and EF Core never sees private setters.
/// </summary>
public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<PaymentRow> Payments => Set<PaymentRow>();

    public DbSet<JournalEntryRow> JournalEntries => Set<JournalEntryRow>();

    public DbSet<JournalLineRow> JournalLines => Set<JournalLineRow>();

    public DbSet<OutboxMessageRow> OutboxMessages => Set<OutboxMessageRow>();

    public DbSet<InboxMessageRow> InboxMessages => Set<InboxMessageRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentRow>(entity =>
        {
            entity.ToTable("payments");
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => p.IdempotencyKey).IsUnique();
            entity.HasIndex(p => p.EndToEndId).IsUnique();
            entity.HasIndex(p => p.Status);
            entity.Property(p => p.Amount).HasPrecision(18, 2);
            entity.Property(p => p.Currency).HasMaxLength(3);
            entity.Property(p => p.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<JournalEntryRow>(entity =>
        {
            entity.ToTable("journal_entries");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PaymentId);
            entity.HasMany(e => e.Lines).WithOne().HasForeignKey(l => l.JournalEntryId);
        });

        modelBuilder.Entity<JournalLineRow>(entity =>
        {
            entity.ToTable("journal_lines");
            entity.HasKey(l => l.Id);
            entity.HasIndex(l => new { l.Account, l.Currency });
            entity.Property(l => l.Amount).HasPrecision(18, 2);
            entity.Property(l => l.Currency).HasMaxLength(3);
        });

        modelBuilder.Entity<OutboxMessageRow>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(m => m.Id);
            entity.HasIndex(m => m.DispatchedAt);
        });

        modelBuilder.Entity<InboxMessageRow>(entity =>
        {
            entity.ToTable("inbox_messages");
            entity.HasKey(m => m.NotificationId);
        });
    }
}

public sealed class PaymentRow
{
    public Guid Id { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    public string RequestHash { get; set; } = string.Empty;

    public string EndToEndId { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string DebtorAccount { get; set; } = string.Empty;

    public string CreditorAccount { get; set; } = string.Empty;

    public string Scheme { get; set; } = string.Empty;

    public int Status { get; set; }

    public string? ReasonCode { get; set; }

    public string? PartnerReference { get; set; }

    public DateOnly? ValueDate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int Version { get; set; }
}

public sealed class JournalEntryRow
{
    public Guid Id { get; set; }

    public Guid PaymentId { get; set; }

    public string Description { get; set; } = string.Empty;

    public DateTimeOffset PostedAt { get; set; }

    public List<JournalLineRow> Lines { get; } = [];
}

public sealed class JournalLineRow
{
    public long Id { get; set; }

    public Guid JournalEntryId { get; set; }

    public string Account { get; set; } = string.Empty;

    public int Side { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;
}

public sealed class OutboxMessageRow
{
    public Guid Id { get; set; }

    public string Type { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset? DispatchedAt { get; set; }
}

public sealed class InboxMessageRow
{
    public string NotificationId { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; set; }
}
