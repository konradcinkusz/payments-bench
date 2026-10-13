using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using PaymentsBench.Api.Validation;
using PaymentsBench.Application.Payments;

namespace PaymentsBench.Api.Contracts;

/// <summary>
/// The body of <c>POST /payments</c>. The limits are ISO 20022 ones where the field is an ISO field:
/// <c>EndToEndId</c> is Max35Text. Validation runs once, generically, from these attributes.
/// </summary>
public sealed record InitiatePaymentRequest(
    [property: Required, MaxLength(35)] string EndToEndId,
    [property: Range(typeof(decimal), "0.01", "9999999999999999.99", ErrorMessage = "Must be greater than zero."), MaxDecimalPlaces(2)] decimal Amount,
    [property: Required, RegularExpression("^[A-Z]{3}$", ErrorMessage = "Must be exactly three uppercase letters.")] string Currency,
    [property: Required, MaxLength(34)] string DebtorAccount,
    [property: Required, MaxLength(34)] string CreditorAccount,
    [property: Required] string Scheme);

/// <summary>
/// The <c>Idempotency-Key</c> header, bound as a type so the generic validation sees its limits;
/// attributes on a handler parameter are not validated.
/// </summary>
public sealed record IdempotencyHeader
{
    [FromHeader(Name = "Idempotency-Key")]
    [Required, MaxLength(255)]
    public string? IdempotencyKey { get; init; }
}

/// <summary>The body of <c>POST /partner/notifications</c>; <c>Kind</c> is a name from <see cref="NotificationKind"/>.</summary>
public sealed record PartnerNotificationRequest(
    [property: Required] string NotificationId,
    [property: Required] string EndToEndId,
    [property: Required, KnownValue<NotificationKind>] string Kind,
    string? ReasonCode,
    DateOnly? ValueDate)
{
    public PartnerNotification ToNotification() =>
        new(NotificationId, EndToEndId, Enum.Parse<NotificationKind>(Kind, ignoreCase: true), ReasonCode, ValueDate);
}

/// <summary>Query string of <c>GET /payments</c>. Paging is clamped, not rejected.</summary>
public sealed record ListPaymentsRequest
{
    [KnownValue<PaymentsBench.Domain.Payments.PaymentStatus>]
    public string? Status { get; init; }

    public string? Scheme { get; init; }

    public string? EndToEndId { get; init; }

    public DateTimeOffset? CreatedFrom { get; init; }

    public DateTimeOffset? CreatedTo { get; init; }

    public int? Page { get; init; }

    public int? Limit { get; init; }
}
