using PaymentsBench.Application.Payments;
using PaymentsBench.Domain;
using PaymentsBench.Domain.Schemes;
using PaymentsBench.Infrastructure.Schemes;

namespace PaymentsBench.Application.Tests;

public class SchemeCatalogTests
{
    private static readonly string RealDirectory = Path.Combine(AppContext.BaseDirectory, "scenarios");

    [Fact]
    public void The_real_files_load_and_every_scheme_resolves_its_calendar()
    {
        var catalog = SchemeCatalogLoader.Load(RealDirectory);

        Assert.Equal(["faster-payments", "sepa", "swift"], catalog.All.Select(s => s.Rules.Name).Order().ToArray());
        Assert.All(catalog.All, s =>
        {
            var calendar = Assert.IsType<HolidayCalendar>(s.Calendar);
            Assert.True(calendar.CoveredFrom <= new DateOnly(2026, 1, 1));
            Assert.True(calendar.CoveredTo >= new DateOnly(2027, 12, 31));
        });
        Assert.Equal("target2", ((HolidayCalendar)catalog.Find("sepa")!.Calendar).Name);
        Assert.Equal("uk-faster-payments", ((HolidayCalendar)catalog.Find("faster-payments")!.Calendar).Name);
        Assert.Equal("us-fedwire", ((HolidayCalendar)catalog.Find("swift")!.Calendar).Name);
        Assert.Null(catalog.Find("nope"));
    }

    [Fact]
    public void Real_target2_data_moves_the_sepa_value_date_over_christmas_and_easter()
    {
        var sepa = SchemeCatalogLoader.Load(RealDirectory).Find("sepa")!;

        Assert.Equal(new DateOnly(2026, 12, 29), ValueDateCalculator.ValueDateFor(sepa.Rules, new DateTimeOffset(2026, 12, 24, 17, 0, 0, TimeSpan.Zero), sepa.Calendar));
        Assert.Equal(new DateOnly(2026, 4, 7), ValueDateCalculator.ValueDateFor(sepa.Rules, new DateTimeOffset(2026, 4, 2, 10, 0, 0, TimeSpan.Zero), sepa.Calendar));
        Assert.Equal(new DateOnly(2027, 3, 31), ValueDateCalculator.ValueDateFor(sepa.Rules, new DateTimeOffset(2027, 3, 25, 17, 0, 0, TimeSpan.Zero), sepa.Calendar)); // Thu after cut-off: Good Friday 26, weekend, Easter Monday 29 skipped; Tue 30 + 1 day
    }

    [Fact]
    public void Faster_payments_runs_every_day()
    {
        var fps = SchemeCatalogLoader.Load(RealDirectory).Find("faster-payments")!;

        Assert.True(fps.Calendar.IsBusinessDay(new DateOnly(2026, 12, 25)));
        Assert.True(fps.Calendar.IsBusinessDay(new DateOnly(2026, 10, 17))); // Saturday
        Assert.Equal(new DateOnly(2026, 10, 17), ValueDateCalculator.ValueDateFor(fps.Rules, new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero), fps.Calendar));
    }

    [Theory]
    [InlineData(2026, 1, 19, false)] // MLK Day
    [InlineData(2026, 7, 3, true)] // 4 July is a Saturday: Reserve Banks stay open the Friday
    [InlineData(2026, 11, 26, false)] // Thanksgiving
    [InlineData(2026, 12, 25, false)]
    [InlineData(2027, 6, 18, true)] // Juneteenth is a Saturday: Friday open
    [InlineData(2027, 7, 5, false)] // 4 July is a Sunday: observed Monday
    [InlineData(2027, 12, 24, true)] // 25 Dec is a Saturday: Friday open
    [InlineData(2027, 12, 31, true)] // 1 Jan 2028 is a Saturday: Friday open
    public void Fedwire_observed_holidays(int year, int month, int day, bool open)
    {
        var fedwire = SchemeCatalogLoader.Load(RealDirectory).Find("swift")!.Calendar;

        Assert.Equal(open, fedwire.IsBusinessDay(new DateOnly(year, month, day)));
    }

    [Fact]
    public void Missing_directory_names_the_directory()
    {
        var ex = Assert.Throws<SchemeDataException>(() => SchemeCatalogLoader.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

        Assert.Contains("Schemes__Directory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_schemes_file_names_the_file()
    {
        using var dir = TempData.Copy(RealDirectory);
        File.Delete(Path.Combine(dir.Path, "schemes.yaml"));

        var ex = Assert.Throws<SchemeDataException>(() => SchemeCatalogLoader.Load(dir.Path));

        Assert.Contains("schemes.yaml", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scheme_naming_a_missing_calendar_is_an_error_naming_the_calendar_file()
    {
        using var dir = TempData.Copy(RealDirectory);
        File.Delete(Path.Combine(dir.Path, "calendars", "target2.yaml"));

        var ex = Assert.Throws<SchemeDataException>(() => SchemeCatalogLoader.Load(dir.Path));

        Assert.Contains("target2.yaml", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sepa", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_calendar_names_the_file()
    {
        using var dir = TempData.Copy(RealDirectory);
        File.WriteAllText(Path.Combine(dir.Path, "calendars", "us-fedwire.yaml"), "name: us-fedwire\nweekendsClosed: [unterminated\n");

        var ex = Assert.Throws<SchemeDataException>(() => SchemeCatalogLoader.Load(dir.Path));

        Assert.Contains("us-fedwire.yaml", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bad_date_in_a_calendar_names_the_file()
    {
        using var dir = TempData.Copy(RealDirectory);
        var path = Path.Combine(dir.Path, "calendars", "target2.yaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("2026-04-03", "03/04/2026", StringComparison.Ordinal));

        var ex = Assert.Throws<SchemeDataException>(() => SchemeCatalogLoader.Load(dir.Path));

        Assert.Contains("target2.yaml", ex.Message, StringComparison.Ordinal);
        Assert.Contains("03/04/2026", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_closing_day_outside_the_covered_range_names_the_file()
    {
        using var dir = TempData.Copy(RealDirectory);
        var path = Path.Combine(dir.Path, "calendars", "target2.yaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("coveredTo: 2027-12-31", "coveredTo: 2027-06-30", StringComparison.Ordinal));

        var ex = Assert.Throws<SchemeDataException>(() => SchemeCatalogLoader.Load(dir.Path));

        Assert.Contains("target2.yaml", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scheme_without_a_calendar_is_an_error_naming_schemes_yaml()
    {
        using var dir = TempData.Copy(RealDirectory);
        var path = Path.Combine(dir.Path, "schemes.yaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("    calendar: target2\n", string.Empty, StringComparison.Ordinal));

        var ex = Assert.Throws<SchemeDataException>(() => SchemeCatalogLoader.Load(dir.Path));

        Assert.Contains("schemes.yaml", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sepa", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_scheme_is_refused_at_initiation_and_nothing_is_written()
    {
        var host = new TestHost();

        var ex = await Assert.ThrowsAsync<UnknownSchemeException>(() => host.Initiate.HandleAsync(TestHost.Command() with { Scheme = "chaps" }, CancellationToken.None));

        Assert.IsAssignableFrom<DomainException>(ex);
        Assert.Empty(host.Outbox.Events);
    }

    [Fact]
    public async Task A_currency_the_scheme_does_not_carry_is_refused_at_initiation()
    {
        var host = new TestHost();

        var ex = await Assert.ThrowsAsync<SchemeCurrencyMismatchException>(() => host.Initiate.HandleAsync(TestHost.Command() with { Currency = "USD" }, CancellationToken.None));

        Assert.IsAssignableFrom<DomainException>(ex);
        Assert.Contains("EUR", ex.Message, StringComparison.Ordinal);
    }

    private sealed class TempData : IDisposable
    {
        private TempData(string path) => Path = path;

        public string Path { get; }

        public static TempData Copy(string source)
        {
            var target = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "schemes-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(System.IO.Path.Combine(target, "calendars"));
            File.Copy(System.IO.Path.Combine(source, "schemes.yaml"), System.IO.Path.Combine(target, "schemes.yaml"));
            foreach (var file in Directory.GetFiles(System.IO.Path.Combine(source, "calendars"), "*.yaml"))
            {
                File.Copy(file, System.IO.Path.Combine(target, "calendars", System.IO.Path.GetFileName(file)));
            }

            return new TempData(target);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
