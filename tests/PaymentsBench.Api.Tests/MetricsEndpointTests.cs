using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace PaymentsBench.Api.Tests;

/// <summary>
/// <c>/metrics</c> is a surface with its own switch (metrics-exposition §5): closed by default,
/// and when open it is anonymous even where the fallback policy demands a token everywhere else.
/// </summary>
public sealed class MetricsEndpointTests
{
    private sealed class MetricsFactory(string? metricsSwitch, bool withAuthority) : ApiFactory
    {
        protected override void ConfigureAdditional(IWebHostBuilder builder)
        {
            builder.UseSetting("Metrics:Prometheus:Enabled", metricsSwitch);
            if (withAuthority)
            {
                // A deployed shape: identity configured, so the fallback policy requires a token.
                builder.UseEnvironment("Production");
                builder.UseSetting("Auth:Authority", "https://auth.test");
                builder.UseSetting("Auth:Issuer", "PaymentsBenchTest");
                builder.UseSetting("Auth:Audience", "PaymentsBenchTest");
            }
        }
    }

    [Fact]
    public async Task Metrics_answers_without_a_token_when_its_switch_is_on_even_with_an_authority()
    {
        using var factory = new MetricsFactory("true", withAuthority: true);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/reconciliation/runs/2026-10-14")).StatusCode);
    }

    [Fact]
    public async Task Metrics_is_not_served_by_default_even_with_an_authority()
    {
        using var factory = new MetricsFactory(null, withAuthority: true);
        using var client = factory.CreateClient();

        // Unmapped: the fallback policy answers 401 before routing says 404. Either way it is not served.
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/metrics")).StatusCode);
    }

    [Fact]
    public async Task Metrics_is_a_404_by_default_and_serves_prometheus_text_when_switched_on()
    {
        using var closed = new MetricsFactory(null, withAuthority: false);
        using var open = new MetricsFactory("true", withAuthority: false);
        using var closedClient = closed.CreateClient();
        using var openClient = open.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await closedClient.GetAsync("/metrics")).StatusCode);
        var response = await openClient.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("target_info", await response.Content.ReadAsStringAsync());
    }
}
