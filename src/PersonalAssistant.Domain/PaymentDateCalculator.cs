namespace PersonalAssistant.Domain;

public static class PaymentDateCalculator
{
    public static DateOnly CalculateNext(DateOnly currentDate, int interval, RecurrenceUnit unit)
        => CalculateNext(currentDate, interval, unit, null, false);

    public static DateOnly CalculateNext(DateOnly currentDate, int interval, RecurrenceUnit unit, int? dayOfMonth, bool isLastDayOfMonth)
    {
        if (interval <= 0)
            throw new ArgumentOutOfRangeException(nameof(interval));

        return unit switch
        {
            RecurrenceUnit.Once => currentDate,
            RecurrenceUnit.Week => currentDate.AddDays(7 * interval),
            RecurrenceUnit.Month => AddMonthsClamped(currentDate, interval, dayOfMonth, isLastDayOfMonth),
            RecurrenceUnit.Year => AddYearsClamped(currentDate, interval, dayOfMonth),
            _ => throw new ArgumentOutOfRangeException(nameof(unit))
        };
    }

    private static DateOnly AddMonthsClamped(DateOnly date, int months, int? dayOfMonth, bool isLastDayOfMonth)
    {
        var target = date.AddMonths(months);
        var day = isLastDayOfMonth
            ? DateTime.DaysInMonth(target.Year, target.Month)
            : Math.Min(dayOfMonth ?? date.Day, DateTime.DaysInMonth(target.Year, target.Month));
        return new DateOnly(target.Year, target.Month, day);
    }

    private static DateOnly AddYearsClamped(DateOnly date, int years, int? dayOfMonth)
    {
        var year = date.Year + years;
        var day = Math.Min(dayOfMonth ?? date.Day, DateTime.DaysInMonth(year, date.Month));
        return new DateOnly(year, date.Month, day);
    }
}
