using System.Globalization;

namespace PersonalAssistant.Bot;

internal static class TelegramPresentation
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static string Money(decimal amount, string currency)
    {
        var value = amount.ToString("#,##0.##", Russian).Replace('\u00A0', ' ');
        return currency.ToUpperInvariant() switch
        {
            "RUB" => $"{value} ₽",
            "USD" => $"{value} $",
            "EUR" => $"{value} €",
            _ => $"{value} {currency.ToUpperInvariant()}"
        };
    }

    public static string Date(DateOnly date, DateOnly today, bool includeYear = false)
    {
        if (date == today)
            return "сегодня";
        if (date == today.AddDays(1))
            return "завтра";

        var format = includeYear || date.Year != today.Year ? "d MMMM yyyy" : "d MMMM";
        return date.ToDateTime(TimeOnly.MinValue).ToString(format, Russian);
    }

    public static string RelativeDays(int days) => days switch
    {
        0 => "сегодня",
        1 => "завтра",
        -1 => "вчера",
        > 1 => $"через {days} дн.",
        _ => $"{Math.Abs(days)} дн. назад"
    };
}
