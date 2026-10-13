namespace PaymentsBench.Scenarios;

/// <summary>A scenario did not do what its file says. The message names the scenario, the step or expectation, and both values.</summary>
public sealed class ScenarioFailedException : Exception
{
    public ScenarioFailedException()
    {
    }

    public ScenarioFailedException(string message)
        : base(message)
    {
    }

    public ScenarioFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
