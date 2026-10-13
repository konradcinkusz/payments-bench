using System.Globalization;
using PaymentsBench.Application.Ports;
using PaymentsBench.Domain.Schemes;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PaymentsBench.Infrastructure.Schemes;

/// <summary>Scheme or calendar data is missing or malformed. The message names the file.</summary>
public sealed class SchemeDataException : Exception
{
    public SchemeDataException(string file, string problem)
        : base($"{file}: {problem}")
    {
    }

    public SchemeDataException()
    {
    }

    public SchemeDataException(string message)
        : base(message)
    {
    }

    public SchemeDataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Reads <c>schemes.yaml</c> and <c>calendars/&lt;name&gt;.yaml</c> from a directory into a
/// <see cref="SchemeCatalog"/>. Scheme data is required: a missing directory or file, a malformed
/// value, an unknown key or a scheme naming a calendar with no file all throw
/// <see cref="SchemeDataException"/> naming the file, so start-up stops instead of running on guesses.
/// </summary>
public static class SchemeCatalogLoader
{
    public const string SchemesFileName = "schemes.yaml";
    public const string CalendarsDirectory = "calendars";

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static SchemeCatalog Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
        {
            throw new SchemeDataException(directory, "scheme data directory does not exist (set Schemes__Directory).");
        }

        var schemesPath = Path.Combine(directory, SchemesFileName);
        var file = Read<SchemesFile>(schemesPath);
        if (file.Schemes is not { Count: > 0 })
        {
            throw new SchemeDataException(schemesPath, "no schemes are defined.");
        }

        var calendars = new Dictionary<string, HolidayCalendar>(StringComparer.Ordinal);
        var schemes = new List<Scheme>();
        foreach (var entry in file.Schemes)
        {
            var name = Require(schemesPath, "name", entry.Name);
            var label = $"scheme '{name}'";
            var currency = Require(schemesPath, $"{label} currency", entry.Currency);
            var cutOffText = Require(schemesPath, $"{label} cutOff", entry.CutOff);
            var calendarName = Require(schemesPath, $"{label} calendar", entry.Calendar);

            if (!TimeOnly.TryParseExact(cutOffText, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var cutOff))
            {
                throw new SchemeDataException(schemesPath, $"{label} cutOff '{cutOffText}' is not HH:mm.");
            }

            if (entry.SettlementBusinessDays is not { } settlementDays || settlementDays < 0)
            {
                throw new SchemeDataException(schemesPath, $"{label} settlementBusinessDays is missing or negative.");
            }

            if (schemes.Exists(s => s.Rules.Name == name))
            {
                throw new SchemeDataException(schemesPath, $"{label} is defined twice.");
            }

            if (!calendars.TryGetValue(calendarName, out var calendar))
            {
                calendar = LoadCalendar(directory, calendarName, label);
                calendars[calendarName] = calendar;
            }

            schemes.Add(new Scheme(new SchemeRules(name, currency, cutOff, settlementDays), calendar));
        }

        return new SchemeCatalog(schemes);
    }

    private static HolidayCalendar LoadCalendar(string directory, string calendarName, string requestedBy)
    {
        if (calendarName.AsSpan().IndexOfAny(['/', '\\']) >= 0 || calendarName.Contains("..", StringComparison.Ordinal))
        {
            throw new SchemeDataException(Path.Combine(directory, SchemesFileName), $"{requestedBy} names calendar '{calendarName}', which is not a plain calendar name.");
        }

        var path = Path.Combine(directory, CalendarsDirectory, calendarName + ".yaml");
        if (!File.Exists(path))
        {
            throw new SchemeDataException(path, $"calendar file not found, but {requestedBy} names calendar '{calendarName}'.");
        }

        var data = Read<CalendarFile>(path);
        if (!string.Equals(data.Name, calendarName, StringComparison.Ordinal))
        {
            throw new SchemeDataException(path, $"name is '{data.Name}', expected '{calendarName}'.");
        }

        var weekendsClosed = data.WeekendsClosed ?? throw new SchemeDataException(path, "weekendsClosed is missing.");
        var from = ParseDate(path, "coveredFrom", data.CoveredFrom);
        var to = ParseDate(path, "coveredTo", data.CoveredTo);
        if (data.ClosingDays is null)
        {
            throw new SchemeDataException(path, "closingDays is missing (use [] for none).");
        }

        var closing = data.ClosingDays.Select(d => ParseDate(path, "closingDays entry", d)).ToList();
        var duplicate = closing.GroupBy(d => d).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new SchemeDataException(path, $"closing day {duplicate.Key:yyyy-MM-dd} is listed twice.");
        }

        try
        {
            return new HolidayCalendar(calendarName, weekendsClosed, from, to, closing);
        }
        catch (ArgumentException ex)
        {
            throw new SchemeDataException(path, ex.Message);
        }
    }

    private static T Read<T>(string path)
    {
        if (!File.Exists(path))
        {
            throw new SchemeDataException(path, "required file not found.");
        }

        try
        {
            return Yaml.Deserialize<T>(File.ReadAllText(path)) ?? throw new SchemeDataException(path, "file is empty.");
        }
        catch (YamlException ex)
        {
            throw new SchemeDataException(path, $"malformed YAML: {ex.Message}");
        }
    }

    private static string Require(string path, string what, string? value) =>
        string.IsNullOrWhiteSpace(value) ? throw new SchemeDataException(path, $"{what} is missing.") : value;

    private static DateOnly ParseDate(string path, string what, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new SchemeDataException(path, $"{what} is missing.");
        }

        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new SchemeDataException(path, $"{what} '{value}' is not a yyyy-MM-dd date.");
    }

    private sealed class SchemesFile
    {
        public List<SchemeEntry>? Schemes { get; set; }
    }

    private sealed class SchemeEntry
    {
        public string? Name { get; set; }

        public string? Currency { get; set; }

        public string? CutOff { get; set; }

        public int? SettlementBusinessDays { get; set; }

        public string? Calendar { get; set; }
    }

    private sealed class CalendarFile
    {
        public string? Name { get; set; }

        public bool? WeekendsClosed { get; set; }

        public string? CoveredFrom { get; set; }

        public string? CoveredTo { get; set; }

        public List<string>? ClosingDays { get; set; }
    }
}
