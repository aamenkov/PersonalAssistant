namespace PersonalAssistant.Application;

public static class DateShortcutCalculator
{
    public static DateOnly Tomorrow(DateOnly today) => today.AddDays(1);
    public static DateOnly FirstDayOfNextMonth(DateOnly today) => new DateOnly(today.Year, today.Month, 1).AddMonths(1);
    public static DateOnly SameDayNextMonth(DateOnly today) => today.AddMonths(1);

    public static bool TryParse(string input, DateOnly today, out DateOnly date)
    {
        date = input.Trim().ToLowerInvariant() switch
        {
            "сегодня" => today,
            "завтра" => Tomorrow(today),
            "через неделю" => today.AddDays(7),
            "первое число следующего месяца" => FirstDayOfNextMonth(today),
            "то же число следующего месяца" => SameDayNextMonth(today),
            _ => default
        };
        return date != default;
    }
}
