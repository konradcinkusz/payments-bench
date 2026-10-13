using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using PaymentsBench.ServiceDefaults;

namespace PaymentsBench.ServiceDefaults.Tests;

public sealed class TelemetryTests
{
    private static async Task<(WebApplication App, HttpClient Client, List<System.Diagnostics.Activity> Spans)> StartAsync(
        Dictionary<string, string?>? settings = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings ?? []);
        builder.AddServiceDefaults();

        var spans = new List<System.Diagnostics.Activity>();
        builder.Services.AddOpenTelemetry().WithTracing(t => t.AddInMemoryExporter(spans));

        var app = builder.Build();
        app.MapDefaultEndpoints();
        app.MapGet("/ping", () => "pong");
        await app.StartAsync();
        return (app, app.GetTestClient(), spans);
    }

    [Fact]
    public async Task Health_checks_are_not_traced_but_ordinary_requests_are()
    {
        var (app, client, spans) = await StartAsync();
        await using var _ = app;

        await client.GetAsync("/health");
        await client.GetAsync("/alive");
        await client.GetAsync("/ping");
        app.Services.GetRequiredService<TracerProvider>().ForceFlush();

        var names = spans.Select(s => s.DisplayName).ToList();
        Assert.Contains(names, n => n.Contains("/ping"));
        Assert.DoesNotContain(names, n => n.Contains("/health") || n.Contains("/alive"));
    }

    [Fact]
    public async Task Metrics_endpoint_is_closed_by_default()
    {
        var (app, client, _) = await StartAsync();
        await using var _a = app;

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/metrics")).StatusCode);
    }

    [Fact]
    public async Task Metrics_endpoint_serves_prometheus_text_when_the_switch_is_on()
    {
        var (app, client, _) = await StartAsync(new() { ["Metrics:Prometheus:Enabled"] = "true" });
        await using var _a = app;

        using var meter = new Meter(TelemetryNames.Meter);
        var transitions = meter.CreateCounter<long>("payments.transitions");
        transitions.Add(1, new KeyValuePair<string, object?>("status", "Submitted"));
        await client.GetAsync("/ping");

        var response = await client.GetAsync("/metrics");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("payments_transitions_total{otel_scope_name=\"PaymentsBench\",status=\"Submitted\"} 1", body);
        // error.type is taken from exception names, which a dependency chooses: it must not be a label.
        Assert.DoesNotContain("error_type", body);
        Assert.Contains("http_route=\"/ping\"", body);
    }
}
