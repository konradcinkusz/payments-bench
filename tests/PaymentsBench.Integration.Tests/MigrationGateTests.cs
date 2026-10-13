using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using PaymentsBench.Infrastructure.Outbox;
using PaymentsBench.Infrastructure.Persistence;

namespace PaymentsBench.Integration.Tests;

/// <summary>The migration service, the gate it completes, the health check that reports it and the dispatcher that waits on it.</summary>
public sealed class MigrationGateTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task The_hosted_service_migrates_after_start_and_opens_the_gate()
    {
        using var host = BuildHost(database.ConnectionString);
        await host.StartAsync();

        var gate = host.Services.GetRequiredService<MigrationGate>();
        await gate.WaitAsync(new CancellationTokenSource(Patience).Token);

        Assert.True(gate.IsCompleted);
        await using var db = database.CreateContext();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var health = await host.Services.GetRequiredService<MigrationHealthCheck>().CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, health.Status);
        await host.StopAsync();
    }

    [Fact]
    public async Task A_failed_migration_faults_the_gate_and_health_stays_unhealthy()
    {
        // Port 1 refuses connections at once: the migration cannot start, the process must stay up.
        using var host = BuildHost("Host=127.0.0.1;Port=1;Username=postgres;Database=nope;Timeout=2;Pooling=false");
        await host.StartAsync();

        var gate = host.Services.GetRequiredService<MigrationGate>();
        await Assert.ThrowsAnyAsync<Exception>(() => gate.WaitAsync(new CancellationTokenSource(Patience).Token));

        Assert.True(gate.IsFaulted);
        Assert.False(gate.IsCompleted);
        var health = await host.Services.GetRequiredService<MigrationHealthCheck>().CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, health.Status);
        await host.StopAsync();
    }

    [Fact]
    public async Task Health_is_unhealthy_while_the_gate_is_closed_and_healthy_once_it_opens()
    {
        var gate = new MigrationGate();
        var check = new MigrationHealthCheck(gate);

        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
        gate.Complete();
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
        Assert.True(MigrationGate.Completed().IsCompleted);
    }

    [Fact]
    public async Task The_dispatcher_publishes_only_after_the_gate_opens()
    {
        await using (var db = database.CreateContext())
        {
            await db.Database.MigrateAsync();
            db.OutboxMessages.Add(new OutboxMessageRow { Id = Guid.NewGuid(), Type = "Probe", Payload = "{}", OccurredAt = Samples.Start });
            await db.SaveChangesAsync();
        }

        var gate = new MigrationGate();
        var publisher = new RecordingPublisher();
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddDbContext<PaymentsDbContext>(o => o.UseNpgsql(database.ConnectionString));
            services.AddSingleton(gate);
            services.AddSingleton<IEventPublisher>(publisher);
            services.AddSingleton<OutboxBatchDispatcher>();
            services.AddSingleton<OutboxDispatcher>();
            services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());
        }).Build();
        await host.StartAsync();

        Assert.Empty(publisher.Published);
        gate.Complete();
        await publisher.FirstPublish.WaitAsync(Patience);
        Assert.Contains("Probe", publisher.Published);
        await host.StopAsync();
    }

    [Fact]
    public async Task The_dispatcher_stops_without_publishing_when_the_gate_faults()
    {
        var gate = new MigrationGate();
        var publisher = new RecordingPublisher();
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddDbContext<PaymentsDbContext>(o => o.UseNpgsql(database.ConnectionString));
            services.AddSingleton(gate);
            services.AddSingleton<IEventPublisher>(publisher);
            services.AddSingleton<OutboxBatchDispatcher>();
            services.AddSingleton<OutboxDispatcher>();
            services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());
        }).Build();
        await host.StartAsync();

        gate.Fail(new InvalidOperationException("migration failed"));
        await host.Services.GetRequiredService<OutboxDispatcher>().ExecuteTask!.WaitAsync(Patience);

        Assert.Empty(publisher.Published);
        await host.StopAsync();
    }

    private static IHost BuildHost(string connectionString) =>
        new HostBuilder().ConfigureServices(services =>
        {
            services.AddDbContext<PaymentsDbContext>(o => o.UseNpgsql(connectionString));
            services.AddSingleton<MigrationGate>();
            services.AddSingleton<MigrationHealthCheck>();
            services.AddHostedService<MigrationHostedService>();
        }).Build();

    private sealed class RecordingPublisher : IEventPublisher
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Published { get; } = [];

        public Task FirstPublish => _first.Task;

        public Task PublishAsync(Guid messageId, string type, string payload, CancellationToken cancellationToken)
        {
            Published.Add(type);
            _first.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
