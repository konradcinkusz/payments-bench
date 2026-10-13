using PaymentsBench.Application.Ports;
using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Application.Reconciliation;

public enum BreakKind
{
    /// <summary>The statement books a movement we have no settled payment for.</summary>
    OnStatementOnly = 0,

    /// <summary>We settled a payment the statement does not show.</summary>
    InLedgerOnly = 1,

    /// <summary>Both sides have it, with different amounts.</summary>
    AmountMismatch = 2,
}

public sealed record ReconciliationBreak(BreakKind Kind, string EndToEndId, decimal? StatementAmount, decimal? LedgerAmount, string Currency);

public sealed record ReconciliationReport(DateOnly Date, int Matched, IReadOnlyList<ReconciliationBreak> Breaks)
{
    public bool IsClean => Breaks.Count == 0;
}

/// <summary>
/// Matches one day's statement against the payments we believe settled that day, by end-to-end
/// id and amount. Breaks are the output, not an alert: each one is an item for someone to work.
/// </summary>
public sealed class ReconciliationService(IPaymentRepository payments, IStatementSource statements)
{
    public async Task<ReconciliationReport> ReconcileAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var statement = await statements.GetAsync(date, cancellationToken);
        var settled = (await payments.ListByStatusAsync(PaymentStatus.Settled, cancellationToken))
            .Where(p => p.ValueDate == date)
            .ToDictionary(p => p.EndToEndId);

        var breaks = new List<ReconciliationBreak>();
        var matched = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in statement.Lines.Where(l => l.Direction == StatementDirection.Debit))
        {
            seen.Add(line.EndToEndId);
            if (!settled.TryGetValue(line.EndToEndId, out var payment))
            {
                breaks.Add(new ReconciliationBreak(BreakKind.OnStatementOnly, line.EndToEndId, line.Amount, null, line.Currency));
                continue;
            }

            if (payment.Amount.Amount != line.Amount || payment.Amount.Currency != line.Currency)
            {
                breaks.Add(new ReconciliationBreak(BreakKind.AmountMismatch, line.EndToEndId, line.Amount, payment.Amount.Amount, line.Currency));
                continue;
            }

            matched++;
        }

        foreach (var payment in settled.Values.Where(p => !seen.Contains(p.EndToEndId)))
        {
            breaks.Add(new ReconciliationBreak(BreakKind.InLedgerOnly, payment.EndToEndId, null, payment.Amount.Amount, payment.Amount.Currency));
        }

        return new ReconciliationReport(date, matched, breaks);
    }
}
