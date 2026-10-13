using PaymentsBench.Application.Ports;
using PaymentsBench.Domain;
using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Integration.Tests;

public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>A scripted, simulated partner: each submit pops the next scripted answer, and every call is recorded.</summary>
public sealed class ScriptedPartner : IPartnerGateway
{
    public Queue<SubmitResult> SubmitAnswers { get; } = new();

    public List<PartnerTransfer> Submits { get; } = [];

    public Task<SubmitResult> SubmitAsync(PartnerTransfer transfer, CancellationToken cancellationToken)
    {
        Submits.Add(transfer);
        return Task.FromResult(SubmitAnswers.Count > 0 ? SubmitAnswers.Dequeue() : new SubmitResult(SubmitOutcome.Accepted, $"P-{Submits.Count}", null));
    }

    public Task<PartnerStatusResult> GetStatusAsync(string endToEndId, CancellationToken cancellationToken) =>
        Task.FromResult(new PartnerStatusResult(PartnerStatus.NotFound, null, null, null));
}

internal static class Samples
{
    public static readonly DateTimeOffset Start = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>A payment walked through the domain to <paramref name="status"/>, with unique key and end-to-end id.</summary>
    public static Payment PaymentIn(PaymentStatus status, decimal amount = 125.50m)
    {
        var at = Start;
        var payment = Payment.Initiate(
            Guid.NewGuid(), Unique("key"), "hash-" + Guid.NewGuid().ToString("N"), Unique("E2E"),
            new Money(amount, "EUR"), "DE89370400440532013000", "GB33BUKB20201555555555", "sepa", at);
        if (status == PaymentStatus.Initiated)
        {
            return payment;
        }

        payment.MarkSubmitted(at = at.AddMinutes(1));
        switch (status)
        {
            case PaymentStatus.Rejected:
                payment.MarkRejected("AC04", at = at.AddMinutes(1));
                break;
            case PaymentStatus.Accepted or PaymentStatus.Settled or PaymentStatus.Returned:
                payment.MarkAccepted("P-" + Guid.NewGuid().ToString("N"), at = at.AddMinutes(1));
                if (status >= PaymentStatus.Settled)
                {
                    payment.MarkSettled(new DateOnly(2026, 10, 14), at = at.AddMinutes(1));
                }

                if (status == PaymentStatus.Returned)
                {
                    payment.MarkReturned("MD06", at.AddMinutes(1));
                }

                break;
            default:
                break;
        }

        return payment;
    }
}
