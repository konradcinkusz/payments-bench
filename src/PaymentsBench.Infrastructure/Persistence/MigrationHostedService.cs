using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PaymentsBench.Infrastructure.Persistence;

/// <summary>
/// Migrates the schema once the server is accepting requests, so <c>/alive</c> answers while it
/// works and <c>/health</c> reports unhealthy until the <see cref="MigrationGate"/> opens. A
/// failed migration is logged and faults the gate; the process stays up and unhealthy rather
/// than crash-looping on a schema problem an operator has to fix.
/// </summary>
public sealed class MigrationHostedService(
    IServiceScopeFactory scopes,
    MigrationGate gate,
    IHostApplicationLifetime lifetime,
    ILogger<MigrationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WaitForServerAsync(stoppingToken);
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            await db.Database.MigrateAsync(stoppingToken);
            logger.LogInformation("Database schema migrated.");
            gate.Complete();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            gate.Fail(new OperationCanceledException("Host stopped before the migration finished."));
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Database migration failed; the service stays unhealthy.");
            gate.Fail(ex);
        }
    }

    private async Task WaitForServerAsync(CancellationToken cancellationToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var startedRegistration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(cancellationToken);
    }
}
