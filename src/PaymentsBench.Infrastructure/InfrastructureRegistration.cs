using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PaymentsBench.Application.Payments;
using PaymentsBench.Application.Ports;
using PaymentsBench.Application.Reconciliation;
using PaymentsBench.Infrastructure.InMemory;
using PaymentsBench.Infrastructure.Outbox;
using PaymentsBench.Infrastructure.Partner;
using PaymentsBench.Infrastructure.Persistence;
using PaymentsBench.Infrastructure.Schemes;
using PaymentsBench.Infrastructure.Sweeping;

namespace PaymentsBench.Infrastructure;

public static class InfrastructureRegistration
{
    public const string PaymentsConnectionName = "payments";
    public const string PartnerClientName = "partner";
    public const string SchemesDirectoryKey = "Schemes:Directory";

    /// <summary>
    /// Wires the ports. With a connection string named <c>payments</c> the stores are PostgreSQL
    /// via EF Core and the outbox has a dispatcher; without one the service runs in memory and
    /// says so in the log, so a missing database degrades the service instead of stopping it.
    /// </summary>
    public static IServiceCollection AddPaymentsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<InitiatePaymentHandler>();
        services.AddScoped<SubmitPaymentHandler>();
        services.AddScoped<ResolveSubmittedPaymentHandler>();
        services.AddScoped<ApplyPartnerNotificationHandler>();
        services.AddScoped<ReconciliationService>();

        // Scheme data is required, so it loads here and a bad file stops start-up with its name (P5, P8).
        var schemesDirectory = configuration[SchemesDirectoryKey] is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(AppContext.BaseDirectory, "scenarios");
        services.AddSingleton<ISchemeCatalog>(SchemeCatalogLoader.Load(schemesDirectory));

        var connectionString = configuration.GetConnectionString(PaymentsConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddSingleton<InMemoryPaymentRepository>();
            services.AddSingleton<IPaymentRepository>(sp => sp.GetRequiredService<InMemoryPaymentRepository>());
            services.AddSingleton<InMemoryLedger>();
            services.AddSingleton<ILedger>(sp => sp.GetRequiredService<InMemoryLedger>());
            services.AddSingleton<InMemoryOutbox>();
            services.AddSingleton<IOutbox>(sp => sp.GetRequiredService<InMemoryOutbox>());
            services.AddSingleton<IInbox, InMemoryInbox>();
            services.AddSingleton<IUnitOfWork, NoOpUnitOfWork>();
            services.AddSingleton(MigrationGate.Completed());
            services.AddSingleton<StorageMode>(new StorageMode("in-memory", Degraded: true));
        }
        else
        {
            services.AddDbContext<PaymentsDbContext>(options => options.UseNpgsql(connectionString));
            services.AddScoped<IPaymentRepository, EfPaymentRepository>();
            services.AddScoped<ILedger, EfLedger>();
            services.AddScoped<IOutbox, EfOutbox>();
            services.AddScoped<IInbox, EfInbox>();
            services.AddScoped<IUnitOfWork, EfUnitOfWork>();
            services.AddOutboxPublisher(configuration);
            services.AddSingleton<MigrationGate>();
            services.AddHostedService<MigrationHostedService>();
            services.AddHostedService<OutboxDispatcher>();
            services.AddSingleton<StorageMode>(new StorageMode("postgresql", Degraded: false));
        }

        services.AddSubmittedPaymentSweeper(configuration);

        services.AddHealthChecks().AddCheck<MigrationHealthCheck>("migrations");

        var partnerBaseAddress = configuration["Partner:BaseAddress"] ?? "http://partner";
        services.AddHttpClient<IPartnerGateway, HttpPartnerGateway>(PartnerClientName, client =>
            {
                client.BaseAddress = new Uri(partnerBaseAddress);
                client.Timeout = TimeSpan.FromSeconds(5);
            })
            .AddStandardResilienceHandler(options =>
            {
                // Submit must not be retried: the handler turns failures into an Unknown outcome
                // and the resolver asks the partner. Retries here apply only to idempotent methods.
                options.Retry.ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Result?.RequestMessage?.Method == HttpMethod.Get
                    && (args.Outcome.Exception is not null || (int?)args.Outcome.Result?.StatusCode >= 500));
                options.Retry.MaxRetryAttempts = 3;
                options.Retry.UseJitter = true;
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(4);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(15);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                options.CircuitBreaker.FailureRatio = 0.5;
                options.CircuitBreaker.MinimumThroughput = 10;
            });

        // The statement format is configuration with a default (P5); a value we do not know stops start-up.
        services.AddSingleton(PartnerStatementSettings.From(configuration[PartnerStatementSettings.ConfigurationKey]));
        services.AddHttpClient<IStatementSource, HttpStatementSource>(client =>
            client.BaseAddress = new Uri(partnerBaseAddress));

        return services;
    }

    public static void LogStorageMode(this IServiceProvider services)
    {
        var mode = services.GetRequiredService<StorageMode>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("PaymentsBench.Storage");
        if (mode.Degraded)
        {
            logger.LogWarning("No '{Connection}' connection string: running DEGRADED with {Mode} storage. Nothing survives a restart.", PaymentsConnectionName, mode.Name);
        }
        else
        {
            logger.LogInformation("Storage: {Mode}.", mode.Name);
        }
    }
}

public sealed record StorageMode(string Name, bool Degraded);
