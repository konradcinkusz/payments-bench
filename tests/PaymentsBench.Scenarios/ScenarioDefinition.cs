using System.Globalization;
using YamlDotNet.Serialization;

namespace PaymentsBench.Scenarios;

/// <summary>One step of a scenario: a name from the vocabulary and its argument, a scalar or a map of scalars.</summary>
public sealed record ScenarioStep(int Number, string Name, string? Scalar, IReadOnlyDictionary<string, string> Arguments)
{
    public string Required(string key) =>
        Arguments.TryGetValue(key, out var value) ? value : throw new ScenarioFailedException($"step {Number} '{Name}' needs '{key}'.");

    public string? Optional(string key) => Arguments.GetValueOrDefault(key);
}

public sealed record PartnerFaultSetting(string Fault, decimal? DriftAmount, string? ReasonCode, int? DelayMilliseconds);

public sealed record ReconciliationExpectation(int? Breaks, IReadOnlyList<string>? Kinds);

/// <summary>Every key the <c>expect</c> block may carry. A key not listed here fails the case.</summary>
public sealed record Expectations(
    string? Status,
    int? Payments,
    int? LedgerEntries,
    IReadOnlyDictionary<string, decimal>? Ledger,
    int? PartnerSubmits,
    int? PartnerStatusQueries,
    ReconciliationExpectation? Reconciliation,
    DateOnly? ValueDate);

/// <summary>
/// A scenario file read into a typed form. Parsing is strict: an unknown top-level key, step name,
/// step argument or <c>expect</c> key fails the case naming the file, so a typo cannot pass as a
/// scenario that asserts nothing.
/// </summary>
public sealed class ScenarioDefinition
{
    public static readonly DateTimeOffset DefaultClock = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    private static readonly string[] TopLevelKeys = ["id", "class", "description", "partner", "clock", "steps", "expect"];
    private static readonly string[] PartnerKeys = ["fault", "driftAmount", "reasonCode", "delayMilliseconds"];
    private static readonly string[] ExpectKeys = ["status", "payments", "ledgerEntries", "ledger", "partnerSubmits", "partnerStatusQueries", "reconciliation", "valueDate"];
    private static readonly string[] ReconciliationKeys = ["breaks", "kinds"];

    /// <summary>The vocabulary: each step name and the argument keys it accepts (empty means a scalar argument).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Vocabulary = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["initiate"] = ["idempotencyKey", "endToEndId", "amount", "currency", "scheme", "debtorAccount", "creditorAccount"],
        ["initiateExpectingConflict"] = ["idempotencyKey", "endToEndId", "amount", "currency", "scheme", "debtorAccount", "creditorAccount"],
        ["submit"] = [],
        ["assertStatus"] = [],
        ["partnerFault"] = ["fault", "driftAmount", "reasonCode", "delayMilliseconds"],
        ["resolve"] = [],
        ["partnerSettles"] = ["endToEndId", "valueDate"],
        ["partnerReturns"] = ["endToEndId", "reasonCode"],
        ["notify"] = ["notificationId", "endToEndId", "kind", "reasonCode", "valueDate"],
        ["reconcile"] = [],
    };

    private ScenarioDefinition(string fileName, string id, string scenarioClass, string description, DateTimeOffset clock, PartnerFaultSetting? partner, IReadOnlyList<ScenarioStep> steps, Expectations expect)
    {
        FileName = fileName;
        Id = id;
        Class = scenarioClass;
        Description = description;
        Clock = clock;
        Partner = partner;
        Steps = steps;
        Expect = expect;
    }

    public string FileName { get; }

    public string Id { get; }

    public string Class { get; }

    public string Description { get; }

    public DateTimeOffset Clock { get; }

    public PartnerFaultSetting? Partner { get; }

    public IReadOnlyList<ScenarioStep> Steps { get; }

    public Expectations Expect { get; }

    public static ScenarioDefinition Load(string path) => Parse(Path.GetFileName(path), File.ReadAllText(path));

    public static ScenarioDefinition Parse(string fileName, string yaml)
    {
        object? root;
        try
        {
            root = new DeserializerBuilder().Build().Deserialize<object>(yaml);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new ScenarioFailedException($"{fileName}: not valid YAML: {ex.Message}", ex);
        }

        var top = AsMap(fileName, "the file", root);
        RejectUnknown(fileName, "the file", top.Keys, TopLevelKeys);

        var id = Text(fileName, top, "id");
        if (id != Path.GetFileNameWithoutExtension(fileName))
        {
            throw new ScenarioFailedException($"{fileName}: id '{id}' must equal the file name.");
        }

        var steps = ParseSteps(fileName, top);
        return new ScenarioDefinition(
            fileName,
            id,
            Text(fileName, top, "class"),
            top.TryGetValue("description", out var description) ? Convert.ToString(description, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty,
            top.TryGetValue("clock", out var clock) ? ParseClock(fileName, clock) : DefaultClock,
            top.TryGetValue("partner", out var partner) ? ParsePartner(fileName, partner) : null,
            steps,
            ParseExpect(fileName, top));
    }

    private static List<ScenarioStep> ParseSteps(string fileName, Dictionary<string, object?> top)
    {
        if (!top.TryGetValue("steps", out var raw) || raw is not List<object?> list || list.Count == 0)
        {
            throw new ScenarioFailedException($"{fileName}: 'steps' must be a non-empty list.");
        }

        var steps = new List<ScenarioStep>();
        foreach (var (item, index) in list.Select((item, index) => (item, index + 1)))
        {
            var entry = AsMap(fileName, $"step {index}", item);
            if (entry.Count != 1)
            {
                throw new ScenarioFailedException($"{fileName}: step {index} must have exactly one key (the step name), found {entry.Count}.");
            }

            var (name, argument) = entry.Single();
            if (!Vocabulary.TryGetValue(name, out var allowed))
            {
                throw new ScenarioFailedException($"{fileName}: unknown step '{name}' (step {index}). Known steps: {string.Join(", ", Vocabulary.Keys)}.");
            }

            if (argument is Dictionary<object, object?>)
            {
                var arguments = AsMap(fileName, $"step {index} '{name}'", argument);
                RejectUnknown(fileName, $"step {index} '{name}'", arguments.Keys, allowed);
                steps.Add(new ScenarioStep(index, name, null, arguments.ToDictionary(p => p.Key, p => Scalar(p.Value), StringComparer.Ordinal)));
            }
            else
            {
                if (allowed.Length > 0 && name != "partnerFault")
                {
                    throw new ScenarioFailedException($"{fileName}: step {index} '{name}' needs a map of {string.Join(", ", allowed)}, not a single value.");
                }

                steps.Add(new ScenarioStep(index, name, Scalar(argument), new Dictionary<string, string>()));
            }
        }

        return steps;
    }

    private static PartnerFaultSetting ParsePartner(string fileName, object? raw)
    {
        var map = AsMap(fileName, "partner", raw);
        RejectUnknown(fileName, "partner", map.Keys, PartnerKeys);
        return new PartnerFaultSetting(
            Text(fileName, map, "fault"),
            map.TryGetValue("driftAmount", out var drift) ? decimal.Parse(Scalar(drift), CultureInfo.InvariantCulture) : null,
            map.TryGetValue("reasonCode", out var reason) ? Scalar(reason) : null,
            map.TryGetValue("delayMilliseconds", out var delay) ? int.Parse(Scalar(delay), CultureInfo.InvariantCulture) : null);
    }

    private static Expectations ParseExpect(string fileName, Dictionary<string, object?> top)
    {
        if (!top.TryGetValue("expect", out var raw))
        {
            throw new ScenarioFailedException($"{fileName}: 'expect' is required.");
        }

        var map = AsMap(fileName, "expect", raw);
        RejectUnknown(fileName, "expect", map.Keys, ExpectKeys);

        Dictionary<string, decimal>? ledger = null;
        if (map.TryGetValue("ledger", out var rawLedger))
        {
            ledger = AsMap(fileName, "expect.ledger", rawLedger).ToDictionary(p => p.Key, p => decimal.Parse(Scalar(p.Value), CultureInfo.InvariantCulture), StringComparer.Ordinal);
        }

        ReconciliationExpectation? reconciliation = null;
        if (map.TryGetValue("reconciliation", out var rawRec))
        {
            var rec = AsMap(fileName, "expect.reconciliation", rawRec);
            RejectUnknown(fileName, "expect.reconciliation", rec.Keys, ReconciliationKeys);
            reconciliation = new ReconciliationExpectation(
                rec.TryGetValue("breaks", out var breaks) ? int.Parse(Scalar(breaks), CultureInfo.InvariantCulture) : null,
                rec.TryGetValue("kinds", out var kinds) ? ((kinds as List<object?>) ?? throw new ScenarioFailedException($"{fileName}: expect.reconciliation.kinds must be a list.")).Select(Scalar).ToList() : null);
        }

        return new Expectations(
            map.TryGetValue("status", out var status) ? Scalar(status) : null,
            Int(map, "payments"),
            Int(map, "ledgerEntries"),
            ledger,
            Int(map, "partnerSubmits"),
            Int(map, "partnerStatusQueries"),
            reconciliation,
            map.TryGetValue("valueDate", out var valueDate) ? DateOnly.ParseExact(Scalar(valueDate), "yyyy-MM-dd", CultureInfo.InvariantCulture) : null);
    }

    private static int? Int(Dictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) ? int.Parse(Scalar(value), CultureInfo.InvariantCulture) : null;

    private static DateTimeOffset ParseClock(string fileName, object? raw) =>
        DateTimeOffset.TryParse(Scalar(raw), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var clock)
            ? clock
            : throw new ScenarioFailedException($"{fileName}: clock '{Scalar(raw)}' is not a timestamp.");

    private static string Text(string fileName, Dictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) && Scalar(value) is { Length: > 0 } text
            ? text
            : throw new ScenarioFailedException($"{fileName}: '{key}' is required.");

    private static string Scalar(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static Dictionary<string, object?> AsMap(string fileName, string where, object? raw) =>
        raw is Dictionary<object, object?> map
            ? map.ToDictionary(p => Scalar(p.Key), p => p.Value, StringComparer.Ordinal)
            : throw new ScenarioFailedException($"{fileName}: {where} must be a map.");

    private static void RejectUnknown(string fileName, string where, IEnumerable<string> keys, string[] allowed)
    {
        var unknown = keys.FirstOrDefault(k => !allowed.Contains(k, StringComparer.Ordinal));
        if (unknown is not null)
        {
            throw new ScenarioFailedException($"{fileName}: unknown key '{unknown}' in {where}. Allowed: {string.Join(", ", allowed)}.");
        }
    }
}
