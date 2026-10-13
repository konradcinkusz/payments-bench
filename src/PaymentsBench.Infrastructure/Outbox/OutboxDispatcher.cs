using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaymentsBench.Infrastructure.Persistence;

namespace PaymentsBench.Infrastructure.Outbox;

/// <summary>
/// Runs <see cref="OutboxBatchDispatcher"/> on a timer. It waits for the <see cref="MigrationGate"/>
/// before its first batch; the at-least-once rules live in the batch dispatcher.
/// </summary>
public sealed class OutboxDispatcher(OutboxBatchDispatcher batches, MigrationGate gate, ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
            logger.LogError(ex, "Outbox dispatcher not started: the schema migration failed.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await batches.DispatchBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbox dispatch failed; will retry.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
