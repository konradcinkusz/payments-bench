using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PaymentsBench.Infrastructure.Sweeping;

namespace PaymentsBench.Application.Tests;

public sealed class SweeperOptionsTests
{
    private static ServiceProvider Provider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSubmittedPaymentSweeperOptions(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Defaults_are_enabled_30_seconds_60_seconds_and_50()
    {
        var options = Provider(new ConfigurationBuilder().Build()).GetRequiredService<IOptions<SweeperOptions>>().Value;

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Interval);
        Assert.Equal(TimeSpan.FromSeconds(60), options.MinimumAge);
        Assert.Equal(50, options.BatchSize);
    }

    [Fact]
    public void Options_bind_from_environment_style_keys()
    {
        // Sweeper__MinimumAge in the environment arrives as "Sweeper:MinimumAge"; a prefix keeps this test off the real environment.
        const string prefix = "PBTEST_";
        var variables = new Dictionary<string, string>
        {
            [$"{prefix}Sweeper__Enabled"] = "false",
            [$"{prefix}Sweeper__Interval"] = "00:00:10",
            [$"{prefix}Sweeper__MinimumAge"] = "00:02:00",
            [$"{prefix}Sweeper__BatchSize"] = "7",
        };
        try
        {
            foreach (var (key, value) in variables)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            var options = Provider(configuration).GetRequiredService<IOptions<SweeperOptions>>().Value;

            Assert.False(options.Enabled);
            Assert.Equal(TimeSpan.FromSeconds(10), options.Interval);
            Assert.Equal(TimeSpan.FromMinutes(2), options.MinimumAge);
            Assert.Equal(7, options.BatchSize);
        }
        finally
        {
            foreach (var key in variables.Keys)
            {
                Environment.SetEnvironmentVariable(key, null);
            }
        }
    }

    [Theory]
    [InlineData("Sweeper:Interval", "00:00:00", "Interval")]
    [InlineData("Sweeper:Interval", "-00:00:05", "Interval")]
    [InlineData("Sweeper:MinimumAge", "00:00:00", "MinimumAge")]
    [InlineData("Sweeper:MinimumAge", "-00:01:00", "MinimumAge")]
    [InlineData("Sweeper:BatchSize", "0", "BatchSize")]
    [InlineData("Sweeper:BatchSize", "501", "BatchSize")]
    [InlineData("Sweeper:BatchSize", "-3", "BatchSize")]
    public void Invalid_values_fail_validation_on_start_and_name_the_key(string key, string value, string expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([new(key, value)]).Build();
        using var provider = Provider(configuration);

        var failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(expected, failure.Message);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("500")]
    public void The_batch_size_bounds_are_inclusive(string batchSize)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([new("Sweeper:BatchSize", batchSize)]).Build();
        using var provider = Provider(configuration);

        provider.GetRequiredService<IStartupValidator>().Validate();
    }
}
