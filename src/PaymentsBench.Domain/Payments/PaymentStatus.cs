namespace PaymentsBench.Domain.Payments;

/// <summary>
/// The lifecycle of an outbound transfer. Every state but <see cref="Initiated"/> is reached by
/// exactly one method on <see cref="Payment"/>, and the allowed moves are listed once, in
/// <see cref="PaymentTransitions"/>.
/// </summary>
public enum PaymentStatus
{
    /// <summary>Accepted from the customer, funds reserved in the ledger, nothing sent yet.</summary>
    Initiated = 0,

    /// <summary>A submit call to the partner has been made. The outcome may still be unknown.</summary>
    Submitted = 1,

    /// <summary>The partner acknowledged the transfer for processing.</summary>
    Accepted = 2,

    /// <summary>Money left the nostro account. Terminal, except for a later return.</summary>
    Settled = 3,

    /// <summary>The partner refused the transfer. Terminal; funds released.</summary>
    Rejected = 4,

    /// <summary>A settled transfer came back from the receiving bank. Terminal; funds released.</summary>
    Returned = 5,
}
