namespace PaymentsBench.Domain.Schemes;

/// <summary>
/// What a payment scheme decides for us: the currency it carries, the daily cut-off after which
/// a submission counts as the next business day, and how many business days settlement takes.
/// Held as data (see <c>scenarios/schemes.yaml</c>), never as branches in code.
/// </summary>
public sealed record SchemeRules(string Name, string Currency, TimeOnly CutOff, int SettlementBusinessDays);

public interface IBusinessCalendar
{
    bool IsBusinessDay(DateOnly day);
}

/// <summary>Weekends only. Kept for tests that want a calendar with no holidays; the service uses <see cref="HolidayCalendar"/>.</summary>
public sealed class WeekendOnlyCalendar : IBusinessCalendar
{
    public bool IsBusinessDay(DateOnly day) => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
}

public static class ValueDateCalculator
{
    /// <summary>
    /// The value date for a submission at <paramref name="submittedAt"/> (already in the scheme's
    /// local time): the same business day if before cut-off, else the next, plus the scheme's
    /// settlement days.
    /// </summary>
    public static DateOnly ValueDateFor(SchemeRules scheme, DateTimeOffset submittedAt, IBusinessCalendar calendar)
    {
        var date = DateOnly.FromDateTime(submittedAt.DateTime);
        if (TimeOnly.FromDateTime(submittedAt.DateTime) >= scheme.CutOff || !calendar.IsBusinessDay(date))
        {
            date = NextBusinessDay(date, calendar);
        }

        for (var i = 0; i < scheme.SettlementBusinessDays; i++)
        {
            date = NextBusinessDay(date, calendar);
        }

        return date;
    }

    private static DateOnly NextBusinessDay(DateOnly date, IBusinessCalendar calendar)
    {
        do
        {
            date = date.AddDays(1);
        }
        while (!calendar.IsBusinessDay(date));

        return date;
    }
}
