using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PaymentsBench.Infrastructure.Persistence;

/// <summary>Unhealthy until the migration gate opens, and still unhealthy if the migration failed.</summary>
public sealed class MigrationHealthCheck(MigrationGate gate) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = gate switch
        {
            { IsCompleted: true } => HealthCheckResult.Healthy("Schema is migrated."),
            { IsFaulted: true } => HealthCheckResult.Unhealthy("Schema migration failed.", gate.Failure),
            _ => HealthCheckResult.Unhealthy("Schema migration is still running."),
        };
        return Task.FromResult(result);
    }
}
