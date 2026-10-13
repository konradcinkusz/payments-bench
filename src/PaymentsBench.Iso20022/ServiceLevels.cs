using System.Collections.Frozen;

namespace PaymentsBench.Iso20022;

/// <summary>
/// The one table that says which ISO 20022 service level (<c>PmtTpInf/SvcLvl/Cd</c>) is which internal
/// scheme. It is a table here, not YAML, because this project takes no third-party package and the
/// YAML reader is one; a service level that is not in it is refused, never guessed. Adding a row is
/// the whole cost of a new one, provided a scheme of that name exists in <c>scenarios/schemes.yaml</c>.
/// </summary>
public static class ServiceLevels
{
    private static readonly FrozenDictionary<string, string> ToScheme =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["SEPA"] = "sepa" }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> FromScheme =
        ToScheme.ToFrozenDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    public static bool TryGetScheme(string serviceLevel, out string scheme) => ToScheme.TryGetValue(serviceLevel, out scheme!);

    public static bool TryGetServiceLevel(string scheme, out string serviceLevel) => FromScheme.TryGetValue(scheme, out serviceLevel!);

    public static IReadOnlyCollection<string> Known => ToScheme.Keys;
}
