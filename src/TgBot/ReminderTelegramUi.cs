using System.Globalization;
using System.Text;
using PersonalAssistant.Application;
using PersonalAssistant.Domain;
using Telegram.Bot.Types.ReplyMarkups;

namespace PersonalAssistant.Bot;

internal static class ReminderTelegramUi
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    internal const int PreviewDays = 45;

    internal static string ModeName(ReminderMode mode) => mode switch
    {
        ReminderMode.Ordinary => "Обычный",
        ReminderMode.Persistent => "Настойчивый",
        ReminderMode.Critical => "Критический",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    internal static string OverdueName(OverdueMode mode) => mode switch
    {
        OverdueMode.Daily => "ежедневно",
        OverdueMode.Frequent => "частые повторы",
        _ => "не напоминать"
    };

    internal static string IntervalName(int minutes) => minutes switch
    {
        60 => "каждый час",
        120 => "каждые 2 часа",
        180 => "каждые 3 часа",
        _ => $"каждые {minutes} мин."
    };

    internal static InlineKeyboardButton Button(string label, string action) => InlineKeyboardButton.WithCallbackData(label, action);

    internal static string FormatProfile(ReminderProfile profile)
    {
        var days = profile.DaysBefore == 0 ? "без предварительного" : $"за {TelegramPresentation.DaysCount(profile.DaysBefore)}";
        var lines = new List<string>
        {
            $"Режим: {ModeName(profile.Mode)}",
            $"Начало: {days}",
            $"Время: {profile.ReminderTimeLocal:HH:mm}"
        };
        if (profile.Mode == ReminderMode.Critical)
        {
            lines.Add($"В день оплаты: {IntervalName(profile.CriticalIntervalMinutes!.Value)}");
            lines.Add($"Активное окно: {profile.CriticalWindowStart:HH:mm}–{profile.CriticalWindowEnd:HH:mm}");
        }
        lines.Add($"После просрочки: {OverdueName(profile.OverdueMode)}");
        return string.Join('\n', lines);
    }

    internal static string FormatPreview(ReminderProfile profile, string zoneId, DateTimeOffset utcNow,
        string? name, DateOnly? dueDate, bool isExample)
    {
        if (name is null || dueDate is null)
            return "📅 Предпросмотр появится после добавления платежа.";

        var zone = TimeZoneResolver.Resolve(zoneId);
        var heading = isExample ? "Пример общего профиля" : "Расписание";
        var sb = new StringBuilder($"📅 {heading}: {name} · срок {dueDate.Value.ToString("d MMMM yyyy", Russian)}\n")
            .Append(TelegramPresentation.TimeZone(zoneId, utcNow));
        sb.Append("\nПо профилю, без учёта отсрочек и доставки.");
        var slots = ReminderScheduleCalculator.Calculate(profile, dueDate.Value, zone, utcNow, utcNow.AddDays(PreviewDays));
        if (slots.Count == 0)
            return sb.Append($"\nНет будущих напоминаний в ближайшие {PreviewDays} дней.").ToString();

        var days = slots.GroupBy(slot => slot.Key.LocalDate).Take(6).ToArray();
        foreach (var day in days.Take(5))
        {
            var items = day.ToArray();
            var groups = new List<string>();
            for (var i = 0; i < items.Length;)
            {
                var start = i++;
                var interval = profile.CriticalIntervalMinutes.GetValueOrDefault();
                while (i < items.Length && interval > 0 && items[start].Key.CriticalTime.HasValue
                    && items[i].Key.CriticalTime.HasValue && IsNext(items[i - 1], items[i], interval))
                    i++;
                groups.Add(i - start == 1
                    ? $"{items[start].LocalTime:HH:mm}"
                    : $"{items[start].LocalTime:HH:mm}–{items[i - 1].LocalTime:HH:mm}, {IntervalName(interval)} ({i - start})");
            }
            sb.Append('\n').Append(day.Key.ToString("d MMMM", Russian)).Append(" — ").AppendJoin("; ", groups);
            if (items[0].Key.Kind == ReminderKind.Overdue)
                sb.Append(" (до оплаты)");
        }
        if (days.Length > 5)
            sb.Append("\nПоказаны ближайшие даты.");
        return sb.ToString();
    }

    private static bool IsNext(ScheduledReminder previous, ScheduledReminder next, int interval) =>
        (next.LocalTime.ToTimeSpan() - previous.LocalTime.ToTimeSpan()).TotalMinutes == interval
        && (next.ScheduledAtUtc - previous.ScheduledAtUtc).TotalMinutes == interval;

    internal static string ShortName(string name) => name.Length <= 34 ? name : $"{name[..32]}…";
}
