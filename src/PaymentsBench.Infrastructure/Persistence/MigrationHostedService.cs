using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PaymentsBench.Infrastructure.Persistence;

/// <summary>
/// Applies the schema on start-up so a fresh database needs no manual step. Until the first
/// EF Core migration is generated this calls EnsureCreated; the register records that as a
/// deviation to close.
/// </summary>
public sealed class MigrationHostedService(IServiceScopeFactory scopes, ILogger<MigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var created = await db.Database.EnsureCreatedAsync(cancellationToken);
        logger.LogInformation("Database schema {Action}.", created ? "created" : "already present");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
