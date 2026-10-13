using PaymentsBench.Application.Ports;
using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Api.Contracts;

public sealed record PaymentView(Guid Id, string EndToEndId, decimal Amount, string Currency, string Status, string? ReasonCode, string? PartnerReference, DateOnly? ValueDate, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static PaymentView From(Payment p) => new(p.Id, p.EndToEndId, p.Amount.Amount, p.Amount.Currency, p.Status.ToString(), p.ReasonCode, p.PartnerReference, p.ValueDate, p.CreatedAt, p.UpdatedAt);
}

public sealed record OutcomeView(string Outcome);

public sealed record BalanceView(string Account, string Currency, decimal Balance);

/// <summary><c>CountsByStatus</c> lists every status, zero included, so the shape does not depend on the data.</summary>
public sealed record PaymentListView(IReadOnlyList<PaymentView> Items, int Page, int Limit, int Total, IReadOnlyDictionary<string, int> CountsByStatus)
{
    public static PaymentListView From(PaymentQuery query, PaymentPage page) => new(
        page.Items.Select(PaymentView.From).ToList(),
        query.Page,
        query.Limit,
        page.Total,
        Enum.GetValues<PaymentStatus>().ToDictionary(s => s.ToString(), s => page.CountsByStatus.GetValueOrDefault(s)));
}

/// <summary>One transaction of a pain.001 document: the payment it became, or the problem that stopped it.</summary>
public sealed record Pain001TransactionView(string EndToEndId, Guid? PaymentId, string? Status, ProblemView? Problem);

public sealed record ProblemView(int Status, string Title, string Detail);

public sealed record Pain001ResultView(IReadOnlyList<Pain001TransactionView> Transactions);
