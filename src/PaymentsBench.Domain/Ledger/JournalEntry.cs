namespace PaymentsBench.Domain.Ledger;

public enum Side
{
    Debit = 0,
    Credit = 1,
}

public sealed record JournalLine(string Account, Side Side, Money Amount);

/// <summary>
/// One balanced, append-only posting. Debits equal credits per currency or the entry does not
/// exist. There is no update and no delete: a mistake is corrected by another entry.
/// </summary>
public sealed record JournalEntry
{
    public JournalEntry(Guid id, Guid paymentId, string description, DateTimeOffset postedAt, IReadOnlyList<JournalLine> lines)
    {
        if (lines.Count < 2)
        {
            throw new DomainException("A journal entry needs at least two lines.");
        }

        foreach (var group in lines.GroupBy(line => line.Amount.Currency))
        {
            var debits = group.Where(l => l.Side == Side.Debit).Sum(l => l.Amount.Amount);
            var credits = group.Where(l => l.Side == Side.Credit).Sum(l => l.Amount.Amount);
            if (debits != credits)
            {
                throw new DomainException($"Journal entry does not balance in {group.Key}: debits {debits}, credits {credits}.");
            }
        }

        if (lines.Any(line => !line.Amount.IsPositive))
        {
            throw new DomainException("Every journal line must carry a positive amount; use the side to express direction.");
        }

        Id = id;
        PaymentId = paymentId;
        Description = description;
        PostedAt = postedAt;
        Lines = lines;
    }

    public Guid Id { get; }

    public Guid PaymentId { get; }

    public string Description { get; }

    public DateTimeOffset PostedAt { get; }

    public IReadOnlyList<JournalLine> Lines { get; }
}
