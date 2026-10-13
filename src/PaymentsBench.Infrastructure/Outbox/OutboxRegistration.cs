using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PaymentsBench.Infrastructure.Outbox;

public static class OutboxRegistration
{
    public const string PublisherKey = "Outbox:Publisher";
    public const string LogPublisher = "log";
    public const string ChannelPublisher = "channel";

    /// <summary>
    /// Registers the batch dispatcher and the publisher chosen by <c>Outbox:Publisher</c>: one
    /// registration per implementation (P10). An unknown value stops start-up and names the
    /// valid ones (P8). The <c>channel</c> choice also runs the deduping consumer in process.
    /// </summary>
    public static IServiceCollection AddOutboxPublisher(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<OutboxBatchDispatcher>();

        var choice = configuration[PublisherKey];
        switch (string.IsNullOrWhiteSpace(choice) ? LogPublisher : choice.Trim().ToLowerInvariant())
        {
            case LogPublisher:
                services.AddSingleton<IEventPublisher, LoggingEventPublisher>();
                break;
            case ChannelPublisher:
                services.AddSingleton<ChannelEventPublisher>();
                services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<ChannelEventPublisher>());
                services.AddSingleton(sp => sp.GetRequiredService<ChannelEventPublisher>().Reader);
                services.AddSingleton<IProcessedMessageStore, InMemoryProcessedMessageStore>();
                services.AddSingleton<IOutboxMessageHandler, LoggingOutboxMessageHandler>();
                services.AddSingleton<DedupingConsumer>();
                services.AddHostedService(sp => sp.GetRequiredService<DedupingConsumer>());
                break;
            default:
                throw new InvalidOperationException(
                    $"{PublisherKey} is '{choice}'; valid values are '{LogPublisher}' and '{ChannelPublisher}'.");
        }

        return services;
    }
}
