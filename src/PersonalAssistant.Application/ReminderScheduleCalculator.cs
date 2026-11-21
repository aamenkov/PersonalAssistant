using PersonalAssistant.Domain;

namespace PersonalAssistant.Application;

// The payment ID is added by the caller. Daily slots keep their identity when their time changes.
public readonly record struct ReminderSlotKey(DateOnly DueDate, DateOnly LocalDate, ReminderKind Kind, TimeOnly? CriticalTime);

public sealed record ScheduledReminder(ReminderSlotKey Key, TimeOnly LocalTime, DateTimeOffset ScheduledAtUtc);

public static class ReminderScheduleCalculator
{
    public static IReadOnlyList<ScheduledReminder> Calculate(
        ReminderProfile profile, DateOnly dueDate, TimeZoneInfo timeZone, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(timeZone);

        var interval = toUtc - fromUtc;
        if (interval < TimeSpan.Zero || interval > TimeSpan.FromDays(366))
            throw new ArgumentOutOfRangeException(nameof(toUtc), "The calculation window must be no longer than 366 days.");
        if (interval == TimeSpan.Zero)
            return [];

        // A local date can be a day earlier or later than its UTC date, including at offset changes.
        var firstDay = Math.Max(DateOnly.MinValue.DayNumber, DateOnly.FromDateTime(fromUtc.UtcDateTime).DayNumber - 1);
        var lastDay = Math.Min(DateOnly.MaxValue.DayNumber, DateOnly.FromDateTime(toUtc.UtcDateTime.AddTicks(-1)).DayNumber + 1);
        var result = new List<ScheduledReminder>();

        for (var day = firstDay; day <= lastDay; day++)
        {
            var date = DateOnly.FromDayNumber(day);
            if (GetKind(profile, dueDate.DayNumber - day) is not { } kind)
                continue;

            var usesCriticalWindow = profile.Mode == ReminderMode.Critical
                && (kind == ReminderKind.DueToday || (kind == ReminderKind.Overdue && profile.OverdueMode == OverdueMode.Frequent));
            if (!usesCriticalWindow)
            {
                AddSlot(date, kind, profile.ReminderTimeLocal, isCritical: false);
                continue;
            }

            var start = profile.CriticalWindowStart!.Value.ToTimeSpan();
            var end = profile.CriticalWindowEnd!.Value.ToTimeSpan();
            var step = TimeSpan.FromMinutes(profile.CriticalIntervalMinutes!.Value);
            for (var time = start; time <= end; time += step)
                AddSlot(date, kind, TimeOnly.FromTimeSpan(time), isCritical: true);
        }

        return result.OrderBy(x => x.ScheduledAtUtc).ToArray();

        void AddSlot(DateOnly date, ReminderKind kind, TimeOnly time, bool isCritical)
        {
            var local = date.ToDateTime(time);
            if (timeZone.IsInvalidTime(local))
            {
                if (isCritical || FindFirstValidMinute(date, time, timeZone) is not { } shifted)
                    return;
                local = shifted;
            }

            // The larger UTC offset is the earlier of the two occurrences of a repeated local time.
            var offset = timeZone.IsAmbiguousTime(local)
                ? timeZone.GetAmbiguousTimeOffsets(local).Max()
                : timeZone.GetUtcOffset(local);
            var utcTicks = local.Ticks - offset.Ticks;
            if (utcTicks < fromUtc.UtcTicks || utcTicks >= toUtc.UtcTicks)
                return;

            var key = new ReminderSlotKey(dueDate, date, kind, isCritical ? time : null);
            result.Add(new ScheduledReminder(key, TimeOnly.FromDateTime(local), new DateTimeOffset(utcTicks, TimeSpan.Zero)));
        }
    }

    private static ReminderKind? GetKind(ReminderProfile profile, int daysUntilDue)
    {
        if (daysUntilDue == 0)
            return ReminderKind.DueToday;
        if (daysUntilDue < 0)
            return profile.OverdueMode == OverdueMode.Disabled ? null : ReminderKind.Overdue;

        var isPreliminary = profile.Mode == ReminderMode.Ordinary
            ? daysUntilDue == profile.DaysBefore
            : daysUntilDue <= profile.DaysBefore;
        return isPreliminary ? ReminderKind.BeforeDue : null;
    }

    private static DateTime? FindFirstValidMinute(DateOnly date, TimeOnly time, TimeZoneInfo timeZone)
    {
        var nextMinute = time.Hour * 60 + time.Minute + 1;
        for (var minute = nextMinute; minute < 24 * 60; minute++)
        {
            var candidate = date.ToDateTime(new TimeOnly(minute / 60, minute % 60));
            if (!timeZone.IsInvalidTime(candidate))
                return candidate;
        }

        return null;
    }
}
