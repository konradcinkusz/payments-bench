using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentsBench.Infrastructure.Outbox;

namespace PaymentsBench.Application.Tests;

public sealed class DedupingConsumerTests
{
    private readonly Channel<OutboxEnvelope> _channel = Channel.CreateUnbounded<OutboxEnvelope>();
    private readonly RecordingHandler _handler = new();
    private readonly InMemoryProcessedMessageStore _store = new();

    private DedupingConsumer Consumer() => new(_channel.Reader, _store, _handler, NullLogger<DedupingConsumer>.Instance);

    [Fact]
    public async Task Each_message_id_is_handled_once_and_redeliveries_are_counted_as_duplicates()
    {
        var a = Envelope();
        var b = Envelope();
        foreach (var message in new[] { a, b, a, a, b })
        {
            await _channel.Writer.WriteAsync(message);
        }

        var consumer = Consumer();
        await consumer.DrainAsync(CancellationToken.None);

        Assert.Equal([a.MessageId, b.MessageId], _handler.Seen.Select(m => m.MessageId));
        Assert.Equal(2, consumer.Applied);
        Assert.Equal(3, consumer.DuplicatesDropped);
    }

    [Fact]
    public async Task Different_messages_with_equal_content_are_not_duplicates()
    {
        await _channel.Writer.WriteAsync(new OutboxEnvelope(Guid.NewGuid(), "T", "{}"));
        await _channel.Writer.WriteAsync(new OutboxEnvelope(Guid.NewGuid(), "T", "{}"));

        var consumer = Consumer();
        await consumer.DrainAsync(CancellationToken.None);

        Assert.Equal(2, _handler.Seen.Count);
        Assert.Equal(0, consumer.DuplicatesDropped);
    }

    [Fact]
    public async Task A_message_whose_handler_threw_is_not_recorded_so_its_redelivery_is_handled()
    {
        var message = Envelope();
        _handler.FailNext = true;
        var consumer = Consumer();

        await consumer.ProcessAsync(message, CancellationToken.None);
        Assert.Equal(0, consumer.Applied);
        Assert.False(_store.Contains(message.MessageId));

        await consumer.ProcessAsync(message, CancellationToken.None);
        Assert.Equal(1, consumer.Applied);
        Assert.Equal(0, consumer.DuplicatesDropped);
        Assert.Single(_handler.Seen);
    }

    [Fact]
    public async Task The_background_loop_consumes_until_stopped()
    {
        var consumer = Consumer();
        await consumer.StartAsync(CancellationToken.None);
        var message = Envelope();
        await _channel.Writer.WriteAsync(message);
        await _channel.Writer.WriteAsync(message);
        _channel.Writer.Complete();

        await consumer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        await consumer.StopAsync(CancellationToken.None);

        Assert.Single(_handler.Seen);
        Assert.Equal(1, consumer.DuplicatesDropped);
    }

    private static OutboxEnvelope Envelope() => new(Guid.NewGuid(), "PaymentInitiated", "{}");

    private sealed class RecordingHandler : IOutboxMessageHandler
    {
        public List<OutboxEnvelope> Seen { get; } = [];

        public bool FailNext { get; set; }

        public Task HandleAsync(OutboxEnvelope message, CancellationToken cancellationToken)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("handler failed");
            }

            Seen.Add(message);
            return Task.CompletedTask;
        }
    }
}
