using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using PaymentsBench.Api;

namespace PaymentsBench.Api.Tests;

/// <summary>
/// Hosts the real API in the <c>Development</c> environment on in-memory storage: the
/// <c>payments</c> connection string is blanked, so no database is needed. One factory is one
/// service instance with its own state; share it per test class through <c>IClassFixture</c>.
/// Further configuration (for example identity settings) goes through <see cref="ConfigureAdditional"/>.
/// </summary>
public class ApiFactory : WebApplicationFactory<ApiEntryPoint>
{
    protected virtual void ConfigureAdditional(IWebHostBuilder builder)
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:payments"] = string.Empty }));
        ConfigureAdditional(builder);
    }
}
