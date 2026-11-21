using System.Globalization;
using PersonalAssistant.Application;
using PersonalAssistant.Domain;

namespace PersonalAssistant.Bot;

internal static class TelegramPresentation
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static string Money(decimal amount, string currency)
    {
        var value = amount.ToString("#,##0.00", Russian).Replace('\u00A0', ' ').Replace('\u202F', ' ');
        var code = currency.ToUpperInvariant();
        return code switch
        {
            "RUB" => $"{value} ₽",
            "USD" => $"{value} $",
            "EUR" => $"{value} €",
            _ => $"{value} {code}"
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

    public static string DaysCount(int days) => $"{days} {RussianDays(days)}";

    private static string RussianDays(int days) => days % 10 == 1 && days % 100 != 11
        ? "день"
        : days % 10 is >= 2 and <= 4 && (days % 100 < 10 || days % 100 >= 20)
            ? "дня"
            : "дней";

    public static string Schedule(int interval, RecurrenceUnit unit, DateOnly nextDate, int scheduledDay, bool isLastDayOfMonth)
    {
        var recurrence = PaymentDisplayNames.Recurrence(interval, unit);
        return unit switch
        {
            RecurrenceUnit.Week => $"{recurrence} по {Weekday(nextDate.DayOfWeek)}",
            RecurrenceUnit.Year => $"{recurrence} {scheduledDay} {Russian.DateTimeFormat.MonthGenitiveNames[nextDate.Month - 1]}",
            RecurrenceUnit.Month when isLastDayOfMonth => $"{recurrence}, в последний день месяца",
            RecurrenceUnit.Month => $"{recurrence}, {scheduledDay} числа",
            _ => recurrence
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

    public static string TimeZone(string timeZoneId, DateTimeOffset? utcNow = null)
    {
        try
        {
            var zone = TimeZoneResolver.Resolve(timeZoneId);
            var offset = zone.GetUtcOffset(utcNow ?? DateTimeOffset.UtcNow);
            var absolute = offset.Duration();
            var sign = offset < TimeSpan.Zero ? "-" : "+";
            var utcOffset = absolute.Minutes == 0 ? $"UTC{sign}{absolute.Hours}" : $"UTC{sign}{absolute.Hours}:{absolute.Minutes:00}";
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
            return timeZoneId == "UTC" ? utcOffset : $"{name} ({utcOffset})";
        }
        catch (TimeZoneNotFoundException)
        {
            return "не настроен";
        }
    }
}
