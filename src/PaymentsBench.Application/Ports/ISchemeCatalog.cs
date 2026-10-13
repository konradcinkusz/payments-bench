using PaymentsBench.Domain.Schemes;

namespace PaymentsBench.Application.Ports;

/// <summary>A payment scheme as the application sees it: its rules and the business calendar they run on.</summary>
public sealed record Scheme(SchemeRules Rules, IBusinessCalendar Calendar);

/// <summary>Every scheme the service knows, loaded from data at start-up. Not finding a scheme is the caller's to refuse.</summary>
public interface ISchemeCatalog
{
    IReadOnlyCollection<Scheme> All { get; }

    /// <summary>The scheme with exactly this name, or null if the catalog has none.</summary>
    Scheme? Find(string name);
}
