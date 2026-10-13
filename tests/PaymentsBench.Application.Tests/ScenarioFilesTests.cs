using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PaymentsBench.Application.Tests;

/// <summary>
/// A cheap, database-free check on the scenario files: every one parses, has an id, a class, at
/// least one step and an expected terminal status that the domain knows. The runner in
/// <c>tests/PaymentsBench.Scenarios</c> is what executes them.
/// </summary>
public class ScenarioFilesTests
{
    private static readonly string[] KnownClasses = ["happy", "partner-failure", "duplicate", "reconciliation"];

    public static TheoryData<string> ScenarioFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in ScenarioFileNames())
        {
            data.Add(file);
        }

        return data;
    }

    private static IEnumerable<string> ScenarioFileNames()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "scenarios");
        return Directory.GetFiles(dir, "*.yaml")
            .Where(f => !f.EndsWith("schemes.yaml", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(Path.GetFileName)!;
    }

    [Theory]
    [MemberData(nameof(ScenarioFiles))]
    public void Every_scenario_file_is_well_formed(string fileName)
    {
        var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
        var scenario = deserializer.Deserialize<ScenarioFile>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenarios", fileName)));

        Assert.Equal(Path.GetFileNameWithoutExtension(fileName), scenario.Id);
        Assert.Contains(scenario.Class, KnownClasses);
        Assert.NotEmpty(scenario.Steps);
        Assert.True(Enum.TryParse<Domain.Payments.PaymentStatus>(scenario.Expect.Status, out _), $"Unknown expected status '{scenario.Expect.Status}'.");
    }

    [Fact]
    public void There_is_at_least_one_scenario_per_class()
    {
        var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
        var classes = ScenarioFileNames()
            .Select(f => deserializer.Deserialize<ScenarioFile>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenarios", f))).Class)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(KnownClasses, c => Assert.Contains(c, classes));
    }

    private sealed class ScenarioFile
    {
        public string Id { get; set; } = string.Empty;

        public string Class { get; set; } = string.Empty;

        public List<object> Steps { get; set; } = [];

        public Expectation Expect { get; set; } = new();
    }

    private sealed class Expectation
    {
        public string Status { get; set; } = string.Empty;
    }
}
