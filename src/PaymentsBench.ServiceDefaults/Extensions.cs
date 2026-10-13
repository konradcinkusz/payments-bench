using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace PaymentsBench.ServiceDefaults;

/// <summary>
/// The shared kernel: OpenTelemetry, health endpoints, service discovery and HTTP resilience
/// defaults. No domain type lives here, by rule.
/// </summary>
public static class Extensions
{
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddServiceDiscovery();
        });
        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var otel = builder.Services.AddOpenTelemetry();

        // Without OTEL_SERVICE_NAME every service would arrive as "unknown_service:dotnet".
        if (string.IsNullOrWhiteSpace(builder.Configuration["OTEL_SERVICE_NAME"]))
        {
            otel.ConfigureResource(resource => resource.AddService(builder.Environment.ApplicationName));
        }

        otel.WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(TelemetryNames.Meter);
                AddBoundedHttpViews(metrics);
                if (PrometheusEnabled(builder.Configuration))
                {
                    metrics.AddPrometheusExporter();
                }
            })
            .WithTracing(tracing => tracing
                .AddSource(TelemetryNames.ActivitySource)
                .AddAspNetCoreInstrumentation(options =>
                    // P15: probes arrive every few seconds and say nothing; they stay out of traces.
                    options.Filter = context => !IsOperationalPath(context.Request.Path))
                .AddHttpClientInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health").AllowAnonymous();
        app.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") }).AllowAnonymous();

        // Metrics-exposition §5: serving /metrics is a configuration decision, closed by default.
        // It is gated by its own switch, so it is explicitly anonymous: a scraper holds no bearer token.
        if (PrometheusEnabled(app.Configuration))
        {
            app.MapPrometheusScrapingEndpoint(MetricsPath).AllowAnonymous();
        }

        return app;
    }

    internal const string MetricsPath = "/metrics";

    /// <summary><c>Metrics:Prometheus:Enabled</c>, default off (env var <c>Metrics__Prometheus__Enabled</c>).</summary>
    private static bool PrometheusEnabled(IConfiguration configuration) =>
        configuration.GetValue<bool>("Metrics:Prometheus:Enabled");

    private static bool IsOperationalPath(PathString path) =>
        path.StartsWithSegments("/health") || path.StartsWithSegments("/alive") || path.StartsWithSegments(MetricsPath);

    /// <summary>
    /// Metrics-exposition §1: the HTTP instruments carry only tags with a bound we hold. Route
    /// templates (never raw paths), methods, status codes and the remote host of our own two
    /// partners are finite; <c>error.type</c> is taken from exception names, which a dependency
    /// chooses, so it is dropped (the status code already says the request failed).
    /// </summary>
    private static void AddBoundedHttpViews(MeterProviderBuilder metrics)
    {
        metrics.AddView("http.server.request.duration", new MetricStreamConfiguration
        {
            TagKeys = ["http.request.method", "http.response.status_code", "http.route", "url.scheme"],
        });
        metrics.AddView("http.client.request.duration", new MetricStreamConfiguration
        {
            TagKeys = ["http.request.method", "http.response.status_code", "server.address", "server.port", "url.scheme"],
        });
    }
}
