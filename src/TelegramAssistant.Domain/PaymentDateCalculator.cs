namespace TelegramAssistant.Domain;

public static class PaymentDateCalculator
{
    public static DateOnly CalculateNext(DateOnly currentDate, int interval, RecurrenceUnit unit)
    {
        if (interval <= 0)
            throw new ArgumentOutOfRangeException(nameof(interval));

        return unit switch
        {
            RecurrenceUnit.Once => currentDate,
            RecurrenceUnit.Week => currentDate.AddDays(7 * interval),
            RecurrenceUnit.Month => AddMonthsClamped(currentDate, interval),
            RecurrenceUnit.Year => AddYearsClamped(currentDate, interval),
            _ => throw new ArgumentOutOfRangeException(nameof(unit))
        };
    }

    private static DateOnly AddMonthsClamped(DateOnly date, int months)
    {
        var target = date.AddMonths(months);
        var day = Math.Min(date.Day, DateTime.DaysInMonth(target.Year, target.Month));
        return new DateOnly(target.Year, target.Month, day);
    }

    private static DateOnly AddYearsClamped(DateOnly date, int years)
    {
        var year = date.Year + years;
        var day = Math.Min(date.Day, DateTime.DaysInMonth(year, date.Month));
        return new DateOnly(year, date.Month, day);
    }
}
