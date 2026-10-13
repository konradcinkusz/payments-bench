using Xunit.Abstractions;

namespace PaymentsBench.Scenarios;

/// <summary>
/// Every <c>scenarios/*.yaml</c> (not <c>schemes.yaml</c>, not <c>calendars/</c>) is one case per
/// storage mode, discovered at run time: a new file is a new case with no code change.
/// </summary>
public class ScenarioTests(ITestOutputHelper output)
{
    public const string PostgresVariable = "PAYMENTSBENCH_TEST_POSTGRES";

    public static bool PostgresConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresVariable));

    public static string ScenarioDirectory => Path.Combine(AppContext.BaseDirectory, "scenarios");

    public static IEnumerable<string> ScenarioFileNames() =>
        Directory.GetFiles(ScenarioDirectory, "*.yaml", SearchOption.TopDirectoryOnly)
            .Select(f => Path.GetFileName(f)!)
            .Where(f => !string.Equals(f, "schemes.yaml", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var file in ScenarioFileNames())
        {
            data.Add(file, StorageKind.InMemory.ToString());
            if (PostgresConfigured)
            {
                data.Add(file, StorageKind.PostgreSql.ToString());
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Scenario_runs_end_to_end(string file, string storage)
    {
        var scenario = ScenarioDefinition.Load(Path.Combine(ScenarioDirectory, file));
        await ScenarioRunner.RunAsync(scenario, Enum.Parse<StorageKind>(storage));
    }

    [Fact]
    public void Storage_modes_that_run()
    {
        var message = PostgresConfigured
            ? $"Scenarios run on in-memory and PostgreSQL storage ({PostgresVariable} is set): {ScenarioFileNames().Count() * 2} cases."
            : $"PostgreSQL scenario cases are NOT generated: {PostgresVariable} is not set. Only the {ScenarioFileNames().Count()} in-memory cases run.";
        output.WriteLine(message);
        Assert.NotEmpty(ScenarioFileNames());
    }
}
