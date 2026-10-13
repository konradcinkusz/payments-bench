namespace PaymentsBench.Application.Ports;

public enum StatementDirection
{
    Debit = 0,
    Credit = 1,
}

/// <summary>One booked movement on the partner's statement, in the shape of a camt.053 entry reduced to what reconciliation needs.</summary>
public sealed record StatementLine(string EndToEndId, decimal Amount, string Currency, DateOnly BookingDate, StatementDirection Direction);

public sealed record Statement(DateOnly Date, string Account, IReadOnlyList<StatementLine> Lines);

public interface IStatementSource
{
    Task<Statement> GetAsync(DateOnly statementDate, CancellationToken cancellationToken);
}
