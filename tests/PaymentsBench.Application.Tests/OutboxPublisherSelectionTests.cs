using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PaymentsBench.Infrastructure.Outbox;

namespace PaymentsBench.Application.Tests;

public sealed class OutboxPublisherSelectionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("log")]
    [InlineData("LOG")]
    public void Log_is_the_default_and_registers_no_consumer(string? value)
    {
        using var provider = Build(value);

        Assert.IsType<LoggingEventPublisher>(provider.GetRequiredService<IEventPublisher>());
        Assert.DoesNotContain(provider.GetServices<IHostedService>(), s => s is DedupingConsumer);
    }

    [Fact]
    public void Channel_registers_the_channel_publisher_and_a_consumer_on_its_reader()
    {
        using var provider = Build("channel");

        var publisher = Assert.IsType<ChannelEventPublisher>(provider.GetRequiredService<IEventPublisher>());
        Assert.Same(publisher, provider.GetRequiredService<ChannelEventPublisher>());
        Assert.Same(publisher.Reader, provider.GetRequiredService<System.Threading.Channels.ChannelReader<OutboxEnvelope>>());
        Assert.Single(provider.GetServices<IHostedService>().OfType<DedupingConsumer>());
    }

    [Fact]
    public async Task The_channel_publisher_carries_the_message_id_unchanged()
    {
        var publisher = new ChannelEventPublisher(capacity: 2);
        var id = Guid.NewGuid();

        await publisher.PublishAsync(id, "PaymentSubmitted", "{\"x\":1}", CancellationToken.None);

        Assert.True(publisher.Reader.TryRead(out var envelope));
        Assert.Equal(new OutboxEnvelope(id, "PaymentSubmitted", "{\"x\":1}"), envelope);
    }

    [Fact]
    public async Task A_full_channel_makes_the_publish_wait_instead_of_dropping()
    {
        var publisher = new ChannelEventPublisher(capacity: 1);
        await publisher.PublishAsync(Guid.NewGuid(), "T", "{}", CancellationToken.None);

        var second = publisher.PublishAsync(Guid.NewGuid(), "T", "{}", CancellationToken.None);
        Assert.False(second.IsCompleted);

        Assert.True(publisher.Reader.TryRead(out _));
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, publisher.PublishedCount);
    }

    [Fact]
    public void An_unknown_value_stops_start_up_and_names_the_valid_ones()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build("kafka"));

        Assert.Contains("'log'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'channel'", ex.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider Build(string? publisher)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(publisher is null ? [] : [new KeyValuePair<string, string?>(OutboxRegistration.PublisherKey, publisher)])
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOutboxPublisher(configuration);
        return services.BuildServiceProvider();
    }
}
