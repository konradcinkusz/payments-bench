using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PaymentsBench.Application.Payments;
using PaymentsBench.Application.Ports;

namespace PaymentsBench.Infrastructure.Sweeping;

public static class SweepingRegistration
{
    /// <summary>The options alone: bound from the <c>Sweeper</c> section and validated on start (P5).</summary>
    public static IServiceCollection AddSubmittedPaymentSweeperOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SweeperOptions>()
            .Bind(configuration.GetSection(SweeperOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SweeperOptions>, SweeperOptionsValidator>());
        return services;
    }

    /// <summary>The sweeper, its options and its metrics. Storage-mode independent.</summary>
    public static IServiceCollection AddSubmittedPaymentSweeper(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSubmittedPaymentSweeperOptions(configuration);
        services.AddMetrics();
        services.TryAddSingleton<SweepMetrics>();
        services.AddSingleton<ISubmittedPaymentResolver, ScopedSubmittedPaymentResolver>();
        services.AddScoped<SweepSubmittedPaymentsHandler>();
        services.AddHostedService<SubmittedPaymentSweeper>();
        return services;
    }
}
