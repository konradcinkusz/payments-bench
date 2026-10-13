using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PaymentsBench.Infrastructure.Outbox;

/// <summary>The consumer's own inbox: the ids it has already applied. Not ours; a real consumer keeps it in its database.</summary>
public interface IProcessedMessageStore
{
    bool Contains(Guid messageId);

    void Add(Guid messageId);
}

public sealed class InMemoryProcessedMessageStore : IProcessedMessageStore
{
    private readonly ConcurrentDictionary<Guid, byte> _ids = new();

    public bool Contains(Guid messageId) => _ids.ContainsKey(messageId);

    public void Add(Guid messageId) => _ids[messageId] = 0;
}

/// <summary>What the consumer does with a message the first time it sees its id.</summary>
public interface IOutboxMessageHandler
{
    Task HandleAsync(OutboxEnvelope message, CancellationToken cancellationToken);
}

public sealed class LoggingOutboxMessageHandler(ILogger<LoggingOutboxMessageHandler> logger) : IOutboxMessageHandler
{
    public Task HandleAsync(OutboxEnvelope message, CancellationToken cancellationToken)
    {
        logger.LogInformation("Consumed {Type} {MessageId}", message.Type, message.MessageId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Reads the channel and invokes the handler once per message id; a redelivery is dropped and
/// counted. The id is recorded after the handler returned, so a handler that throws is not
/// treated as applied and the message may be handled again if it is redelivered.
/// </summary>
public sealed class DedupingConsumer(
    ChannelReader<OutboxEnvelope> reader,
    IProcessedMessageStore store,
    IOutboxMessageHandler handler,
    ILogger<DedupingConsumer> logger) : BackgroundService
{
    private long _applied;
    private long _duplicates;

    public long Applied => Interlocked.Read(ref _applied);

    public long DuplicatesDropped => Interlocked.Read(ref _duplicates);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in reader.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(message, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // stopping
        }
    }

    /// <summary>Processes everything already in the channel and returns; lets a test run the consumer without timing.</summary>
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (reader.TryRead(out var message))
        {
            await ProcessAsync(message, cancellationToken);
        }
    }

    public async Task ProcessAsync(OutboxEnvelope message, CancellationToken cancellationToken)
    {
        if (store.Contains(message.MessageId))
        {
            Interlocked.Increment(ref _duplicates);
            logger.LogInformation("Dropped duplicate {Type} {MessageId}", message.Type, message.MessageId);
            return;
        }

        try
        {
            await handler.HandleAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Handler failed for {Type} {MessageId}; not recorded as applied.", message.Type, message.MessageId);
            return;
        }

        store.Add(message.MessageId);
        Interlocked.Increment(ref _applied);
    }
}
