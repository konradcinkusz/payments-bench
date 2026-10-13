using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace PaymentsBench.Infrastructure.Outbox;

/// <summary>
/// What an implementation must honour: <see cref="PublishAsync"/> returns only once the
/// destination holds the message (the dispatcher marks the row dispatched right after); the
/// message id is the outbox row id and must reach the consumer unchanged; messages are offered
/// in <c>OccurredAt</c> order, best effort. See ADR 0009.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync(Guid messageId, string type, string payload, CancellationToken cancellationToken);
}

/// <summary>The default publisher: a log line. Nothing consumes it.</summary>
public sealed class LoggingEventPublisher(ILogger<LoggingEventPublisher> logger) : IEventPublisher
{
    public Task PublishAsync(Guid messageId, string type, string payload, CancellationToken cancellationToken)
    {
        logger.LogInformation("Published {Type} {MessageId}: {Payload}", type, messageId, payload);
        return Task.CompletedTask;
    }
}

/// <summary>What travels between publisher and consumer. <paramref name="MessageId"/> is the outbox row id.</summary>
public sealed record OutboxEnvelope(Guid MessageId, string Type, string Payload);

/// <summary>
/// Publishes onto a bounded in-process channel, so the delivery contract can be tested without a
/// broker. A full channel makes <see cref="PublishAsync"/> wait, which is the back-pressure a
/// broker client would apply. It is not durable: a restart loses what was in the channel, which
/// is why it narrows risk R5 and does not close it.
/// </summary>
public sealed class ChannelEventPublisher : IEventPublisher
{
    public const int DefaultCapacity = 1024;

    private readonly Channel<OutboxEnvelope> _channel;
    private long _published;

    public ChannelEventPublisher(int capacity = DefaultCapacity)
    {
        _channel = Channel.CreateBounded<OutboxEnvelope>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    public ChannelReader<OutboxEnvelope> Reader => _channel.Reader;

    /// <summary>How many times <see cref="PublishAsync"/> has put a message on the channel, redeliveries included.</summary>
    public long PublishedCount => Interlocked.Read(ref _published);

    public async Task PublishAsync(Guid messageId, string type, string payload, CancellationToken cancellationToken)
    {
        await _channel.Writer.WriteAsync(new OutboxEnvelope(messageId, type, payload), cancellationToken);
        Interlocked.Increment(ref _published);
    }
}
