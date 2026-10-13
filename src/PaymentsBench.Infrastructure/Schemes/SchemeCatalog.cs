using PaymentsBench.Application.Ports;

namespace PaymentsBench.Infrastructure.Schemes;

/// <summary>The loaded schemes, immutable for the life of the process.</summary>
public sealed class SchemeCatalog : ISchemeCatalog
{
    private readonly Dictionary<string, Scheme> byName;

    public SchemeCatalog(IEnumerable<Scheme> schemes)
    {
        ArgumentNullException.ThrowIfNull(schemes);
        byName = schemes.ToDictionary(s => s.Rules.Name, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<Scheme> All => byName.Values;

    public Scheme? Find(string name) => name is not null && byName.TryGetValue(name, out var scheme) ? scheme : null;
}
