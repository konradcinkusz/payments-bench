using PaymentsBench.Application.Ports;

namespace PaymentsBench.Application.Payments;

/// <summary>Resolve up to <paramref name="BatchSize"/> payments that have sat at Submitted for at least <paramref name="MinimumAge"/>.</summary>
public sealed record SweepSubmittedPayments(TimeSpan MinimumAge, int BatchSize);

public sealed record SweepFailure(Guid PaymentId, Exception Exception);

/// <param name="Examined">How many payments the batch selected.</param>
/// <param name="Outcomes">Resolutions by <see cref="ResolutionOutcome"/> name; failures are not in here.</param>
/// <param name="Failures">Payments whose resolve threw, with the exception.</param>
/// <param name="OldestSubmittedUpdatedAt">The last update of the oldest payment still at Submitted after the batch; null when none.</param>
/// <param name="OldestSubmittedAge">That payment's age when the sweep finished; zero when none.</param>
public sealed record SweepResult(
    int Examined,
    IReadOnlyDictionary<ResolutionOutcome, int> Outcomes,
    IReadOnlyList<SweepFailure> Failures,
    DateTimeOffset? OldestSubmittedUpdatedAt,
    TimeSpan OldestSubmittedAge);

/// <summary>
/// Resolves one payment in a unit of work of its own. The infrastructure implementation opens a
/// DI scope per call, so one payment's DbContext is never shared with another's.
/// </summary>
public interface ISubmittedPaymentResolver
{
    Task<ResolutionOutcome> ResolveAsync(Guid paymentId, CancellationToken cancellationToken);
}

/// <summary>
/// The scheduled form of the recovery in ADR 0001. It selects, oldest first, the Submitted payments
/// whose last update is older than the minimum age and hands each to <see cref="ISubmittedPaymentResolver"/>,
/// which is <see cref="ResolveSubmittedPaymentHandler"/>: the sweep never submits anything itself,
/// so a payment is resubmitted only when the partner says it has no record. One payment's failure
/// is recorded and the batch goes on.
/// </summary>
public sealed class SweepSubmittedPaymentsHandler(IPaymentRepository payments, ISubmittedPaymentResolver resolver, IClock clock)
{
    public async Task<SweepResult> HandleAsync(SweepSubmittedPayments command, CancellationToken cancellationToken)
    {
        var window = await payments.ListSubmittedBeforeAsync(clock.UtcNow - command.MinimumAge, command.BatchSize, cancellationToken);

        var outcomes = new Dictionary<ResolutionOutcome, int>();
        var failures = new List<SweepFailure>();
        foreach (var id in window.Ids)
        {
            try
            {
                var outcome = await resolver.ResolveAsync(id, cancellationToken);
                outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(new SweepFailure(id, ex));
            }
        }

        // Ask again after the batch: the oldest one may just have been resolved.
        var oldest = window.Ids.Count == 0
            ? window.OldestUpdatedAt
            : (await payments.ListSubmittedBeforeAsync(clock.UtcNow - command.MinimumAge, 0, cancellationToken)).OldestUpdatedAt;
        var age = oldest is { } at ? clock.UtcNow - at : TimeSpan.Zero;
        return new SweepResult(window.Ids.Count, outcomes, failures, oldest, age < TimeSpan.Zero ? TimeSpan.Zero : age);
    }
}
