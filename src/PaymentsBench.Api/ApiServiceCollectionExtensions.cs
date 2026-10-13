using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Serialization;
using PaymentsBench.Api.Endpoints;
using PaymentsBench.Api.Errors;
using PaymentsBench.Api.Identity;

namespace PaymentsBench.Api;

public static class ApiServiceCollectionExtensions
{
    /// <summary>
    /// Everything the HTTP surface needs: OpenAPI, request validation, problem details and the
    /// exception handler that feeds them, JSON enum names, and the telemetry sources.
    /// </summary>
    public static IServiceCollection AddPaymentsApi(this IServiceCollection services)
    {
        services.AddOpenApi();
        services.AddValidation();
        services.AddProblemDetails();
        services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        // Built by factory so the container owns them and disposes them at shutdown.
        services.AddSingleton(_ => new ActivitySource("PaymentsBench"));
        services.AddSingleton(_ => new Meter("PaymentsBench"));
        services.AddSingleton<PaymentMetrics>();
        return services;
    }

    /// <summary>The request pipeline that goes with <see cref="AddPaymentsApi"/>: errors first, so everything below is covered.</summary>
    public static WebApplication UsePaymentsApi(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.MapOpenApi().AllowAnonymousInDevelopment(app.Environment);
        app.MapPaymentsEndpoints();
        app.MapPain001Endpoints();
        app.MapPartnerNotificationEndpoints();
        app.MapReconciliationEndpoints();
        app.MapLedgerEndpoints();
        return app;
    }
}
