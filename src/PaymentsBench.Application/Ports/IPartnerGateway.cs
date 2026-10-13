namespace PaymentsBench.Application.Ports;

public enum SubmitOutcome
{
    /// <summary>The partner acknowledged the transfer and returned its reference.</summary>
    Accepted = 0,

    /// <summary>The partner refused the transfer with a reason code.</summary>
    Rejected = 1,

    /// <summary>
    /// The call did not complete: a timeout, a dropped connection, a 5xx after the body was sent.
    /// The transfer may or may not exist on the partner side. The only safe next step is to ask.
    /// </summary>
    Unknown = 2,
}

public sealed record SubmitResult(SubmitOutcome Outcome, string? PartnerReference, string? ReasonCode);

public enum PartnerStatus
{
    /// <summary>The partner has no record of this end-to-end id. Resubmitting is safe.</summary>
    NotFound = 0,
    Accepted = 1,
    Settled = 2,
    Rejected = 3,
    Returned = 4,
}

public sealed record PartnerStatusResult(PartnerStatus Status, string? PartnerReference, string? ReasonCode, DateOnly? ValueDate);

public sealed record PartnerTransfer(string EndToEndId, decimal Amount, string Currency, string DebtorAccount, string CreditorAccount, string Scheme);

/// <summary>
/// The partner as the application sees it. Submit is not idempotent on the wire, which is why
/// its unknown outcome exists; status lookup by end-to-end id is, which is why it is the recovery.
/// </summary>
public interface IPartnerGateway
{
    Task<SubmitResult> SubmitAsync(PartnerTransfer transfer, CancellationToken cancellationToken);

    Task<PartnerStatusResult> GetStatusAsync(string endToEndId, CancellationToken cancellationToken);
}
