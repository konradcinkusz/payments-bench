using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PaymentsBench.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c>. Generating a migration reads the model, never the database, so
/// the connection string has no password and no server needs to be running.
/// </summary>
public sealed class PaymentsDbContextFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql("Host=localhost;Database=paymentsbench_design;Username=postgres")
            .Options);
}
