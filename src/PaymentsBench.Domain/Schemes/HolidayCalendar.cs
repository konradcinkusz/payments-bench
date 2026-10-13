namespace PaymentsBench.Domain.Schemes;

/// <summary>A date outside the range a calendar covers: the calendar does not know, so it does not guess.</summary>
public sealed class CalendarCoverageException : DomainException
{
    public CalendarCoverageException(string calendar, DateOnly day, DateOnly coveredFrom, DateOnly coveredTo)
        : base($"Calendar '{calendar}' covers {coveredFrom:yyyy-MM-dd} to {coveredTo:yyyy-MM-dd}; {day:yyyy-MM-dd} is outside it, so whether it is a business day is unknown.")
    {
    }

    public CalendarCoverageException()
    {
    }

    public CalendarCoverageException(string message)
        : base(message)
    {
    }

    public CalendarCoverageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A scheme's business calendar, built from data: whether weekends are closed, the explicit
/// closing days, and the range the data covers (both ends inclusive). Asking about a date outside
/// the range throws <see cref="CalendarCoverageException"/> instead of answering "business day".
/// </summary>
public sealed class HolidayCalendar : IBusinessCalendar
{
    private readonly HashSet<DateOnly> closingDays;

    public HolidayCalendar(string name, bool weekendsClosed, DateOnly coveredFrom, DateOnly coveredTo, IEnumerable<DateOnly> closingDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(closingDays);
        if (coveredTo < coveredFrom)
        {
            throw new ArgumentException($"Calendar '{name}': coveredTo {coveredTo:yyyy-MM-dd} is before coveredFrom {coveredFrom:yyyy-MM-dd}.", nameof(coveredTo));
        }

        this.closingDays = [.. closingDays];
        var outside = this.closingDays.Where(d => d < coveredFrom || d > coveredTo).Order().FirstOrDefault();
        if (outside != default)
        {
            throw new ArgumentException($"Calendar '{name}': closing day {outside:yyyy-MM-dd} is outside the covered range {coveredFrom:yyyy-MM-dd} to {coveredTo:yyyy-MM-dd}.", nameof(closingDays));
        }

        Name = name;
        WeekendsClosed = weekendsClosed;
        CoveredFrom = coveredFrom;
        CoveredTo = coveredTo;
    }

    public string Name { get; }

    public bool WeekendsClosed { get; }

    public DateOnly CoveredFrom { get; }

    public DateOnly CoveredTo { get; }

    public IReadOnlyCollection<DateOnly> ClosingDays => closingDays;

    public bool IsBusinessDay(DateOnly day)
    {
        if (day < CoveredFrom || day > CoveredTo)
        {
            throw new CalendarCoverageException(Name, day, CoveredFrom, CoveredTo);
        }

        if (WeekendsClosed && day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        return !closingDays.Contains(day);
    }
}
