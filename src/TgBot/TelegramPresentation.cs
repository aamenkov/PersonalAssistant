using System.Globalization;
using PersonalAssistant.Application;
using PersonalAssistant.Domain;

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
        > 1 => $"через {days} {RussianDays(days)}",
        _ => $"{Math.Abs(days)} {RussianDays(Math.Abs(days))} назад"
    };

    private static string RussianDays(int days) => days % 10 == 1 && days % 100 != 11
        ? "день"
        : days % 10 is >= 2 and <= 4 && (days % 100 < 10 || days % 100 >= 20)
            ? "дня"
            : "дней";

    public static string Schedule(int interval, RecurrenceUnit unit, DateOnly nextDate, DateOnly today)
    {
        var recurrence = PaymentDisplayNames.Recurrence(interval, unit);
        return unit switch
        {
            RecurrenceUnit.Once => "Разовый платеж",
            RecurrenceUnit.Week => $"{recurrence} по {Weekday(nextDate.DayOfWeek)}",
            RecurrenceUnit.Year => $"{recurrence} {nextDate.Day} {nextDate.ToString("MMMM", Russian)}",
            _ => $"{recurrence}, {nextDate.Day} числа"
        };
    }

    private static string Weekday(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "понедельникам",
        DayOfWeek.Tuesday => "вторникам",
        DayOfWeek.Wednesday => "средам",
        DayOfWeek.Thursday => "четвергам",
        DayOfWeek.Friday => "пятницам",
        DayOfWeek.Saturday => "субботам",
        _ => "воскресеньям"
    };

    public static string TimeZone(string timeZoneId)
    {
        try
        {
            var zone = TimeZoneResolver.Resolve(timeZoneId);
            var offset = zone.GetUtcOffset(DateTime.UtcNow);
            var hours = offset.TotalHours >= 0 ? $"+{offset.TotalHours:0}" : $"{offset.TotalHours:0}";
            var name = timeZoneId switch
            {
                "Europe/Moscow" => "Москва",
                "Europe/Berlin" => "Берлин",
                "Asia/Almaty" => "Алматы",
                "Asia/Tokyo" => "Токио",
                "America/New_York" => "Нью-Йорк",
                "UTC" => "UTC",
                _ when TimeZoneResolver.TryParseFixedOffset(timeZoneId, out _) => "Ваш регион",
                _ => timeZoneId
            };
            return $"{name} · UTC{hours}";
        }
        catch (TimeZoneNotFoundException)
        {
            return "не настроен";
        }
    }
}
