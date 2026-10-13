using PaymentsBench.Application.Ports;

namespace PaymentsBench.Application.Tests;

public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>A scripted partner: each submit and status call pops the next scripted answer, and every call is counted.</summary>
public sealed class ScriptedPartner : IPartnerGateway
{
    public Queue<SubmitResult> SubmitAnswers { get; } = new();

    public Queue<PartnerStatusResult> StatusAnswers { get; } = new();

    public List<PartnerTransfer> Submits { get; } = [];

    public List<string> StatusQueries { get; } = [];

    /// <summary>End-to-end ids whose status lookup throws, as a partner that is down for one transfer would.</summary>
    public HashSet<string> ThrowOnStatusFor { get; } = [];

    public Task<SubmitResult> SubmitAsync(PartnerTransfer transfer, CancellationToken cancellationToken)
    {
        Submits.Add(transfer);
        return Task.FromResult(SubmitAnswers.Count > 0 ? SubmitAnswers.Dequeue() : new SubmitResult(SubmitOutcome.Accepted, $"P-{Submits.Count}", null));
    }

    public Task<PartnerStatusResult> GetStatusAsync(string endToEndId, CancellationToken cancellationToken)
    {
        StatusQueries.Add(endToEndId);
        if (ThrowOnStatusFor.Contains(endToEndId))
        {
            throw new InvalidOperationException($"Simulated partner failure for {endToEndId}.");
        }

        return Task.FromResult(StatusAnswers.Count > 0 ? StatusAnswers.Dequeue() : new PartnerStatusResult(PartnerStatus.NotFound, null, null, null));
    }
}

public sealed class FixedStatements(Statement statement) : IStatementSource
{
    public Task<Statement> GetAsync(DateOnly statementDate, CancellationToken cancellationToken) => Task.FromResult(statement);
}
