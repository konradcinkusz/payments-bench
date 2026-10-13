using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PaymentsBench.Api;
using PaymentsBench.Application.Ports;
using PaymentsBench.Integration.Tests;
using PaymentsBench.PartnerSimulator;

namespace PaymentsBench.Scenarios;

public enum StorageKind
{
    InMemory = 0,
    PostgreSql = 1,
}

public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

/// <summary>
/// A fresh pair for one scenario: the API and the simulated partner, both in process through
/// <see cref="WebApplicationFactory{TEntryPoint}"/>. The API's partner <c>HttpClient</c>s (the
/// gateway's named client and the statement source's typed client) are routed to the simulator's
/// <c>TestServer</c> handler, so every call crosses real HTTP serialisation and the real
/// resilience pipeline, without a socket.
/// </summary>
public sealed class ScenarioHost : IAsyncDisposable
{
    /// <summary>The partner timeouts a scenario run uses: short, so a timeout scenario finishes in seconds.</summary>
    public static readonly IReadOnlyDictionary<string, string?> PartnerTimeouts = new Dictionary<string, string?>
    {
        ["Partner:Timeout"] = "00:00:02",
        ["Partner:AttemptTimeout"] = "00:00:01",
        ["Partner:TotalTimeout"] = "00:00:04",
    };

    /// <summary>How long the simulated partner holds a response after accepting, when a scenario does not say.</summary>
    public const int DefaultTimeoutAfterAcceptMilliseconds = 3000;

    private readonly SimulatorFactory simulator;
    private readonly ApiHostFactory api;
    private readonly PostgresFixture? database;

    private ScenarioHost(ApiHostFactory api, PostgresFixture? database, SimulatorFactory simulator)
    {
        this.api = api;
        this.database = database;
        this.simulator = simulator;
        Api = api.CreateClient();
        Partner = simulator.CreateClient();
    }

    public HttpClient Api { get; }

    public HttpClient Partner { get; }

    public ISchemeCatalog Schemes => api.Services.GetRequiredService<ISchemeCatalog>();

    public static async Task<ScenarioHost> StartAsync(StorageKind storage, DateTimeOffset clock)
    {
        var simulator = new SimulatorFactory();
        PostgresFixture? database = null;
        if (storage == StorageKind.PostgreSql)
        {
            database = new PostgresFixture();
            await database.InitializeAsync();
        }

        var api = new ApiHostFactory(simulator, database?.ConnectionString ?? string.Empty, clock);
        var host = new ScenarioHost(api, database, simulator);
        if (storage == StorageKind.PostgreSql)
        {
            await host.WaitUntilHealthyAsync();
        }

        return host;
    }

    public async ValueTask DisposeAsync()
    {
        Api.Dispose();
        Partner.Dispose();
        await api.DisposeAsync();
        await simulator.DisposeAsync();
        if (database is not null)
        {
            await database.DisposeAsync();
        }
    }

    /// <summary>The service's own hosted migrator runs after start-up; <c>/health</c> is unhealthy until it has finished.</summary>
    private async Task WaitUntilHealthyAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var last = "no response";
        while (true)
        {
            var response = await Api.GetAsync("/health", timeout.Token);
            last = $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(timeout.Token)}";
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"The API did not become healthy within 60 s on PostgreSQL; last /health answer: {last}");
            }
        }
    }

    private sealed class SimulatorFactory : WebApplicationFactory<SimulatorEntryPoint>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");
    }

    private sealed class ApiHostFactory(SimulatorFactory simulator, string connectionString, DateTimeOffset clock) : WebApplicationFactory<ApiEntryPoint>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Development with no Auth:Authority: the API allows anonymous calls (ADR 0008).
            builder.UseEnvironment("Development");
            // UseSetting, not ConfigureAppConfiguration: Program.cs reads these while it builds the
            // services, before a configuration callback would apply.
            foreach (var (key, value) in new Dictionary<string, string?>(PartnerTimeouts)
            {
                ["ConnectionStrings:payments"] = connectionString,
                ["Auth:Authority"] = string.Empty,
                ["Partner:BaseAddress"] = "http://simulated-partner",
                // A background sweep would race the scenario's explicit resolve steps.
                ["Sweeper:Enabled"] = "false",
            })
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IClock>(new FixedClock(clock));
                services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => simulator.Server.CreateHandler()));
            });
        }
    }
}
