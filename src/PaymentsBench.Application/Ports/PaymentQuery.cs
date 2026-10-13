using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Application.Ports;

/// <summary>
/// A list query over payments. <see cref="CreatedFrom"/> and <see cref="CreatedTo"/> are both
/// inclusive. Build it with <see cref="Create"/>, which clamps paging so no caller can ask a store
/// for an unbounded page.
/// </summary>
public sealed record PaymentQuery
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    public PaymentStatus? Status { get; init; }

    public string? Scheme { get; init; }

    public string? EndToEndId { get; init; }

    public DateTimeOffset? CreatedFrom { get; init; }

    public DateTimeOffset? CreatedTo { get; init; }

    public int Page { get; init; } = 1;

    public int Limit { get; init; } = DefaultLimit;

    public int Skip => (Page - 1) * Limit;

    /// <summary><c>page = max(1, page)</c>, <c>limit = clamp(limit, 1, 100)</c>, default limit 20.</summary>
    public static PaymentQuery Create(
        PaymentStatus? status = null,
        string? scheme = null,
        string? endToEndId = null,
        DateTimeOffset? createdFrom = null,
        DateTimeOffset? createdTo = null,
        int? page = null,
        int? limit = null) => new()
        {
            Status = status,
            Scheme = scheme,
            EndToEndId = endToEndId,
            CreatedFrom = createdFrom,
            CreatedTo = createdTo,
            Page = Math.Max(1, page ?? 1),
            Limit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit),
        };
}

/// <summary>
/// One page of a list query. <see cref="Total"/> and <see cref="CountsByStatus"/> describe the whole
/// filtered set, not the page; statuses with no payments are absent from the counts.
/// </summary>
public sealed record PaymentPage(
    IReadOnlyList<Payment> Items,
    int Total,
    IReadOnlyDictionary<PaymentStatus, int> CountsByStatus);
