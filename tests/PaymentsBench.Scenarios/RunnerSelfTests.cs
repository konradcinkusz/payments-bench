namespace PaymentsBench.Scenarios;

/// <summary>The runner refuses what it does not understand and says what failed, where. A runner that shrugs at a typo proves nothing.</summary>
public class RunnerSelfTests
{
    private const string Header = "id: sample\nclass: happy\npartner:\n  fault: none\n";

    private const string Initiate = """
          - initiate:
              idempotencyKey: s-1
              endToEndId: E2E-S-1
              amount: 10.00
              currency: EUR
              scheme: sepa
        """;

    [Fact]
    public void An_unknown_step_name_fails_naming_the_step_and_the_file()
    {
        var yaml = $"{Header}steps:\n  - teleport: E2E-S-1\nexpect:\n  status: Initiated\n";

        var failure = Assert.Throws<ScenarioFailedException>(() => ScenarioDefinition.Parse("sample.yaml", yaml));

        Assert.Contains("unknown step 'teleport'", failure.Message);
        Assert.Contains("sample.yaml", failure.Message);
    }

    [Fact]
    public void An_unknown_expect_key_fails_naming_the_key_and_the_file()
    {
        var yaml = $"{Header}steps:\n{Initiate}\nexpect:\n  status: Initiated\n  mystery: 1\n";

        var failure = Assert.Throws<ScenarioFailedException>(() => ScenarioDefinition.Parse("sample.yaml", yaml));

        Assert.Contains("unknown key 'mystery'", failure.Message);
        Assert.Contains("sample.yaml", failure.Message);
    }

    [Fact]
    public void An_unknown_step_argument_and_an_unknown_top_level_key_fail()
    {
        var argument = $"{Header}steps:\n  - initiate:\n      endToEndId: E2E-S-1\n      colour: red\nexpect:\n  status: Initiated\n";
        var top = $"{Header}flavour: x\nsteps:\n{Initiate}\nexpect:\n  status: Initiated\n";

        Assert.Contains("unknown key 'colour'", Assert.Throws<ScenarioFailedException>(() => ScenarioDefinition.Parse("sample.yaml", argument)).Message);
        Assert.Contains("unknown key 'flavour'", Assert.Throws<ScenarioFailedException>(() => ScenarioDefinition.Parse("sample.yaml", top)).Message);
    }

    [Fact]
    public async Task A_wrong_expectation_fails_naming_scenario_step_expected_and_actual()
    {
        var yaml = $"{Header}steps:\n{Initiate}\nexpect:\n  status: Settled\n";

        var failure = await Assert.ThrowsAsync<ScenarioFailedException>(() =>
            ScenarioRunner.RunAsync(ScenarioDefinition.Parse("sample.yaml", yaml), StorageKind.InMemory));

        Assert.Contains("Scenario 'sample'", failure.Message);
        Assert.Contains("expect.status expected Settled but was Initiated", failure.Message);
    }

    [Fact]
    public async Task A_failing_step_names_the_step()
    {
        var yaml = $"{Header}steps:\n{Initiate}\n  - assertStatus: Settled\nexpect:\n  status: Initiated\n";

        var failure = await Assert.ThrowsAsync<ScenarioFailedException>(() =>
            ScenarioRunner.RunAsync(ScenarioDefinition.Parse("sample.yaml", yaml), StorageKind.InMemory));

        Assert.Contains("step 2 'assertStatus'", failure.Message);
        Assert.Contains("expected Settled but was Initiated", failure.Message);
    }

    [Fact]
    public void Discovery_excludes_the_scheme_rules_and_the_calendars()
    {
        var files = ScenarioTests.ScenarioFileNames().ToList();

        Assert.DoesNotContain("schemes.yaml", files);
        Assert.DoesNotContain(files, f => f.Contains("target2", StringComparison.Ordinal));
        Assert.Contains("happy-sepa-transfer.yaml", files);
    }
}
