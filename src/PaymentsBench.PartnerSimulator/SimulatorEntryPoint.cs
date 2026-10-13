namespace PaymentsBench.PartnerSimulator;

/// <summary>
/// A named handle on this assembly for <c>WebApplicationFactory&lt;T&gt;</c>, for the same reason
/// the API has one: both services have a top-level <c>Program</c>, and a test project that hosts
/// both needs two distinct names. The scenario runner hosts the simulated partner through it.
/// </summary>
public sealed class SimulatorEntryPoint;
