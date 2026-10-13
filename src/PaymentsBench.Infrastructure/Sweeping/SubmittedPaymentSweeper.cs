using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentsBench.Application.Payments;
using PaymentsBench.Infrastructure.Persistence;

namespace PaymentsBench.Infrastructure.Sweeping;

/// <summary>
/// Resolves payments left at Submitted after an unknown outcome (ADR 0001), on a timer. It waits
/// for the <see cref="MigrationGate"/>, then sweeps every <see cref="SweeperOptions.Interval"/>.
/// The work is <see cref="SweepSubmittedPaymentsHandler"/>; this class only schedules it and reports it.
/// </summary>
public sealed class SubmittedPaymentSweeper(
    IServiceScopeFactory scopes,
    MigrationGate gate,
    IOptions<SweeperOptions> options,
    SweepMetrics metrics,
    ILogger<SubmittedPaymentSweeper> logger) : BackgroundService
{
    public static readonly ActivitySource ActivitySource = new(SweepMetrics.MeterName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Submitted-payment sweeper is disabled ({Key}:Enabled=false).", SweeperOptions.SectionName);
            return;
        }

        try
        {
            await gate.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Submitted-payment sweeper not started: the schema migration failed.");
            return;
        }

        logger.LogInformation(
            "Submitted-payment sweeper started: every {Interval}, minimum age {MinimumAge}, batch {BatchSize}.",
            settings.Interval, settings.MinimumAge, settings.BatchSize);

        using var timer = new PeriodicTimer(settings.Interval);
        do
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Submitted-payment sweep failed; will retry in {Interval}.", settings.Interval);
            }
        }
        while (await WaitForNextAsync(timer, stoppingToken));
    }

    /// <summary>One sweep: a batch, its metrics and its log line. Public so a test can drive it without a timer.</summary>
    public async Task<SweepResult> SweepOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var activity = ActivitySource.StartActivity("payments.sweep");

        using var scope = scopes.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<SweepSubmittedPaymentsHandler>();
        var result = await handler.HandleAsync(new SweepSubmittedPayments(settings.MinimumAge, settings.BatchSize), cancellationToken);

        metrics.Record(result);
        activity?.SetTag("sweep.examined", result.Examined);
        activity?.SetTag("sweep.failed", result.Failures.Count);
        activity?.SetTag("sweep.oldest_age_seconds", result.OldestSubmittedAge.TotalSeconds);

        foreach (var failure in result.Failures)
        {
            logger.LogWarning(failure.Exception, "Sweep could not resolve payment {PaymentId}; it stays Submitted for the next sweep.", failure.PaymentId);
        }

        if (result.Examined > 0)
        {
            logger.LogInformation(
                "Sweep examined {Examined} Submitted payment(s): {Outcomes}, {Failed} failed; oldest still Submitted {OldestAgeSeconds:F0}s.",
                result.Examined,
                string.Join(", ", result.Outcomes.Select(o => $"{o.Key}={o.Value}")),
                result.Failures.Count,
                result.OldestSubmittedAge.TotalSeconds);
        }

        return result;
    }

    private static async Task<bool> WaitForNextAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
