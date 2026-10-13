using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaymentsBench.Infrastructure.Persistence;

namespace PaymentsBench.Infrastructure.Outbox;

/// <summary>
/// Polls the outbox and hands each message to <see cref="IEventPublisher"/>, marking it
/// dispatched only after the publish returned. Consumers must dedupe on the message id: this
/// is at-least-once by design. It waits for the <see cref="MigrationGate"/> before its first batch.
/// </summary>
public sealed class OutboxDispatcher(IServiceScopeFactory scopes, IEventPublisher publisher, MigrationGate gate, ILogger<OutboxDispatcher> logger) : BackgroundService
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
                await DispatchBatchAsync(stoppingToken);
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

    private async Task DispatchBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var pending = await db.OutboxMessages
            .Where(m => m.DispatchedAt == null)
            .OrderBy(m => m.OccurredAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var message in pending)
        {
            await publisher.PublishAsync(message.Id, message.Type, message.Payload, cancellationToken);
            message.DispatchedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}

public interface IEventPublisher
{
    Task PublishAsync(Guid messageId, string type, string payload, CancellationToken cancellationToken);
}

/// <summary>The skeleton's publisher: a log line. A broker is a one-class swap behind this interface.</summary>
public sealed class LoggingEventPublisher(ILogger<LoggingEventPublisher> logger) : IEventPublisher
{
    public Task PublishAsync(Guid messageId, string type, string payload, CancellationToken cancellationToken)
    {
        logger.LogInformation("Published {Type} {MessageId}: {Payload}", type, messageId, payload);
        return Task.CompletedTask;
    }
}
