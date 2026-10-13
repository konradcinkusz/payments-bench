using PaymentsBench.Domain.Schemes;

namespace PaymentsBench.Domain.Tests;

public class HolidayCalendarTests
{
    private static readonly SchemeRules Sepa = new("sepa", "EUR", new TimeOnly(16, 0), SettlementBusinessDays: 1);

    /// <summary>TARGET2 closing days for 2026 as the ECB publishes them; the real data file is tested in the Application tests.</summary>
    private static readonly HolidayCalendar Target2 = new(
        "target2",
        weekendsClosed: true,
        coveredFrom: new DateOnly(2026, 1, 1),
        coveredTo: new DateOnly(2026, 12, 31),
        closingDays: [new(2026, 1, 1), new(2026, 4, 3), new(2026, 4, 6), new(2026, 5, 1), new(2026, 12, 25), new(2026, 12, 26)]);

    [Fact]
    public void Christmas_moves_the_sepa_value_date_not_just_the_weekend()
    {
        var submitted = new DateTimeOffset(2026, 12, 24, 17, 0, 0, TimeSpan.Zero); // Thursday, after cut-off

        // Next business day skips Fri 25 Dec (T2 closed), Sat, Sun: Mon 28 Dec; one settlement day: Tue 29 Dec.
        Assert.Equal(new DateOnly(2026, 12, 29), ValueDateCalculator.ValueDateFor(Sepa, submitted, Target2));

        // Weekends only would have said Monday 28 Dec, so the holiday is what moved it.
        var weekendsOnly = new HolidayCalendar("weekends", true, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), []);
        Assert.Equal(new DateOnly(2026, 12, 28), ValueDateCalculator.ValueDateFor(Sepa, submitted, weekendsOnly));
    }

    [Fact]
    public void Good_friday_and_easter_monday_are_skipped()
    {
        var submitted = new DateTimeOffset(2026, 4, 2, 10, 0, 0, TimeSpan.Zero); // Thursday, before cut-off

        Assert.Equal(new DateOnly(2026, 4, 7), ValueDateCalculator.ValueDateFor(Sepa, submitted, Target2));
    }

    [Theory]
    [InlineData(2026, 1, 1)]
    [InlineData(2026, 4, 3)]
    [InlineData(2026, 4, 6)]
    [InlineData(2026, 5, 1)]
    [InlineData(2026, 12, 25)]
    public void Closing_days_are_not_business_days(int year, int month, int day) =>
        Assert.False(Target2.IsBusinessDay(new DateOnly(year, month, day)));

    [Fact]
    public void A_weekend_is_still_closed()
    {
        Assert.False(Target2.IsBusinessDay(new DateOnly(2026, 10, 17))); // Saturday
        Assert.False(Target2.IsBusinessDay(new DateOnly(2026, 10, 18))); // Sunday
        Assert.True(Target2.IsBusinessDay(new DateOnly(2026, 10, 19))); // Monday
    }

    [Fact]
    public void Open_weekends_are_business_days()
    {
        var always = new HolidayCalendar("always", weekendsClosed: false, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), []);

        Assert.True(always.IsBusinessDay(new DateOnly(2026, 10, 17)));
    }

    [Theory]
    [InlineData(2025, 12, 31)]
    [InlineData(2027, 1, 1)]
    public void A_date_outside_coverage_throws_instead_of_being_a_business_day(int year, int month, int day) =>
        Assert.Throws<CalendarCoverageException>(() => Target2.IsBusinessDay(new DateOnly(year, month, day)));

    [Fact]
    public void Coverage_ends_are_inclusive()
    {
        Assert.False(Target2.IsBusinessDay(new DateOnly(2026, 1, 1))); // closing day, but known
        Assert.True(Target2.IsBusinessDay(new DateOnly(2026, 12, 31))); // Thursday, last covered day
    }

    [Fact]
    public void A_value_date_that_runs_past_coverage_throws()
    {
        var submitted = new DateTimeOffset(2026, 12, 30, 17, 0, 0, TimeSpan.Zero); // after cut-off: 31 Dec, then 1 Jan 2027 is unknown

        Assert.Throws<CalendarCoverageException>(() => ValueDateCalculator.ValueDateFor(Sepa, submitted, Target2));
    }

    [Fact]
    public void The_coverage_exception_is_a_domain_exception() =>
        Assert.IsAssignableFrom<DomainException>(new CalendarCoverageException("c", new DateOnly(2030, 1, 1), new DateOnly(2026, 1, 1), new DateOnly(2027, 12, 31)));

    [Fact]
    public void A_closing_day_outside_the_range_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new HolidayCalendar("c", true, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), [new DateOnly(2027, 1, 1)]));
        Assert.Throws<ArgumentException>(() => new HolidayCalendar("c", true, new DateOnly(2026, 12, 31), new DateOnly(2026, 1, 1), []));
    }
}
