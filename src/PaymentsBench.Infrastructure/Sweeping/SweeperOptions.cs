using Microsoft.Extensions.Options;

namespace PaymentsBench.Infrastructure.Sweeping;

/// <summary>Bound from the <c>Sweeper</c> section, so <c>Sweeper__MinimumAge=00:02:00</c> works from the environment (P5).</summary>
public sealed class SweeperOptions
{
    public const string SectionName = "Sweeper";
    public const int MaxBatchSize = 500;

    public bool Enabled { get; set; } = true;

    /// <summary>How often a sweep runs.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A payment is swept only when it has sat at Submitted at least this long, so the sweep does not race the submit that is still in flight.</summary>
    public TimeSpan MinimumAge { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The most payments one sweep resolves.</summary>
    public int BatchSize { get; set; } = 50;
}

public sealed class SweeperOptionsValidator : IValidateOptions<SweeperOptions>
{
    public ValidateOptionsResult Validate(string? name, SweeperOptions options)
    {
        var failures = new List<string>();
        if (options.Interval <= TimeSpan.Zero)
        {
            failures.Add($"{SweeperOptions.SectionName}:Interval must be positive, was {options.Interval}.");
        }

        if (options.MinimumAge <= TimeSpan.Zero)
        {
            failures.Add($"{SweeperOptions.SectionName}:MinimumAge must be positive, was {options.MinimumAge}.");
        }

        if (options.BatchSize is < 1 or > SweeperOptions.MaxBatchSize)
        {
            failures.Add($"{SweeperOptions.SectionName}:BatchSize must be between 1 and {SweeperOptions.MaxBatchSize}, was {options.BatchSize}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
