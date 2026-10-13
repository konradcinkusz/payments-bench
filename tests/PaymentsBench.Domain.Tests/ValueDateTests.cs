using PaymentsBench.Domain.Schemes;

namespace PaymentsBench.Domain.Tests;

public class ValueDateTests
{
    private static readonly SchemeRules Sepa = new("sepa", "EUR", new TimeOnly(16, 0), SettlementBusinessDays: 1);
    private static readonly WeekendOnlyCalendar Calendar = new();

    [Fact]
    public void Before_cut_off_on_a_business_day_settles_next_business_day()
    {
        var submitted = new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero); // Tuesday

        Assert.Equal(new DateOnly(2026, 10, 14), ValueDateCalculator.ValueDateFor(Sepa, submitted, Calendar));
    }

    [Fact]
    public void After_cut_off_counts_as_the_next_day()
    {
        var submitted = new DateTimeOffset(2026, 10, 13, 16, 0, 0, TimeSpan.Zero); // Tuesday, exactly at cut-off

        Assert.Equal(new DateOnly(2026, 10, 15), ValueDateCalculator.ValueDateFor(Sepa, submitted, Calendar));
    }

    [Fact]
    public void Weekends_are_skipped()
    {
        var submitted = new DateTimeOffset(2026, 10, 16, 17, 0, 0, TimeSpan.Zero); // Friday after cut-off

        Assert.Equal(new DateOnly(2026, 10, 20), ValueDateCalculator.ValueDateFor(Sepa, submitted, Calendar));
    }
}
