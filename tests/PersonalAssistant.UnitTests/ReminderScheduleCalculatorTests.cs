using PersonalAssistant.Application;
using PersonalAssistant.Domain;

namespace PersonalAssistant.UnitTests;

public sealed class ReminderScheduleCalculatorTests
{
    private static readonly DateOnly DueDate = new(2026, 12, 20);
    private static readonly TimeOnly Morning = new(9, 0);

    [Theory]
    [InlineData(3, true)]
    [InlineData(0, false)]
    public void Ordinary_SendsSinglePreliminarySlotAndKeepsDueDayWhenDaysAreZero(int daysBefore, bool hasPreliminary)
    {
        // Check the legacy cadence, including the disabled preliminary notification.
        var profile = new ReminderProfile(ReminderMode.Ordinary, daysBefore, Morning, OverdueMode.Daily);
        var slots = ForDates(profile, DueDate.AddDays(-4), DueDate.AddDays(2));
        var expected = hasPreliminary
            ? new[] { ReminderKind.BeforeDue, ReminderKind.DueToday, ReminderKind.Overdue }
            : new[] { ReminderKind.DueToday, ReminderKind.Overdue };

        Assert.Equal(expected, slots.Select(x => x.Key.Kind));
        if (hasPreliminary)
            Assert.Equal(DueDate.AddDays(-3), slots[0].Key.LocalDate);
    }

    [Fact]
    public void Persistent_StartsAtConfiguredBoundaryAndCanDisableOverdueDays()
    {
        // The start day and due day are included, but days outside the configured range are absent.
        var profile = new ReminderProfile(ReminderMode.Persistent, 3, Morning, OverdueMode.Disabled);
        var slots = ForDates(profile, DueDate.AddDays(-4), DueDate.AddDays(2));

        Assert.Equal(Enumerable.Range(-3, 4).Select(DueDate.AddDays), slots.Select(x => x.Key.LocalDate));
        Assert.Equal(ReminderKind.DueToday, slots[^1].Key.Kind);
        Assert.All(slots, slot => Assert.Null(slot.Key.CriticalTime));
    }

    [Theory]
    [InlineData(60, 13)]
    [InlineData(120, 7)]
    [InlineData(180, 5)]
    public void Critical_WindowIncludesOnGridEndpointsAndDoesNotDuplicateTheMorning(int interval, int expectedCount)
    {
        // The critical window replaces the regular due-day notification.
        var slots = ForDates(Critical(interval), DueDate.AddDays(-4), DueDate.AddDays(2));
        var critical = slots.Where(x => x.Key.Kind == ReminderKind.DueToday).ToArray();
        Assert.Equal(Enumerable.Range(-3, 3).Select(DueDate.AddDays),
            slots.Where(x => x.Key.Kind == ReminderKind.BeforeDue).Select(x => x.Key.LocalDate));
        Assert.Equal(expectedCount, critical.Length);
        Assert.Equal(Morning, critical[0].LocalTime);
        Assert.Equal(new TimeOnly(21, 0), critical[^1].LocalTime);
        Assert.All(critical.Zip(critical.Skip(1)), pair => Assert.Equal(TimeSpan.FromMinutes(interval), pair.Second.LocalTime - pair.First.LocalTime));
        Assert.All(critical, slot => Assert.Equal<TimeOnly?>(slot.LocalTime, slot.Key.CriticalTime));
        Assert.Equal(Morning, Assert.Single(slots, x => x.Key.Kind == ReminderKind.Overdue).LocalTime);
    }

    [Fact]
    public void Critical_FrequentOverdueUsesWindowWithoutAddingAnOffGridEndpoint()
    {
        // Hourly overdue repeats use the same window and interval as the due day.
        var profile = Critical(180, new TimeOnly(9, 30), OverdueMode.Frequent, new TimeOnly(16, 0));
        var slots = ForDates(profile, DueDate.AddDays(1), DueDate.AddDays(2));

        Assert.Equal(new TimeOnly[] { new(9, 30), new(12, 30), new(15, 30) }, slots.Select(x => x.LocalTime));
        Assert.All(slots, slot => Assert.Equal(ReminderKind.Overdue, slot.Key.Kind));
    }

    [Fact]
    public void SlotIdentity_TracksCriticalWallTimeAcrossIntervalAndWindowEdits()
    {
        // A send at 10:00 cannot use the identity of the new 11:00 slot after changing the interval.
        var hourly = ForDates(Critical(60), DueDate, DueDate.AddDays(1));
        var twoHourly = ForDates(Critical(120), DueDate, DueDate.AddDays(1));
        var shifted = ForDates(Critical(60, new TimeOnly(10, 0)), DueDate, DueDate.AddDays(1));

        Assert.Contains(twoHourly, slot => slot.Key == hourly[2].Key); // 11:00 stays 11:00.
        Assert.DoesNotContain(twoHourly, slot => slot.Key == hourly[1].Key); // 10:00 is no longer in the window grid.
        Assert.Equal(hourly[1].Key, shifted[0].Key); // Moving the start does not renumber 10:00.
        Assert.NotEqual(hourly[0].Key, shifted[0].Key);
    }

    [Fact]
    public void SlotIdentity_DailyTimeAndModeChangesKeepTheSameLogicalEvent()
    {
        // Daily events retain their deduplication key even when scheduled at a different hour.
        var oldProfile = new ReminderProfile(ReminderMode.Ordinary, 3, Morning, OverdueMode.Daily);
        var newProfile = new ReminderProfile(ReminderMode.Persistent, 3, new TimeOnly(11, 0), OverdueMode.Daily);
        var oldSlot = Assert.Single(ForDates(oldProfile, DueDate, DueDate.AddDays(1)));
        var newSlot = Assert.Single(ForDates(newProfile, DueDate, DueDate.AddDays(1)));

        Assert.Equal(oldSlot.Key, newSlot.Key);
        Assert.NotEqual(oldSlot.ScheduledAtUtc, newSlot.ScheduledAtUtc);
        Assert.NotEqual(oldSlot.Key, ForDates(Critical(60), DueDate, DueDate.AddDays(1))[0].Key);
    }

    [Fact]
    public void Interval_IsHalfOpenAndUsesTheUsersLocalDateAcrossMidnight()
    {
        // A UTC day boundary must not change the local payment period or make the upper endpoint inclusive.
        var zone = TimeZoneResolver.Resolve("UTC+03:00");
        var profile = new ReminderProfile(ReminderMode.Ordinary, 0, new TimeOnly(0, 15), OverdueMode.Disabled);
        var moment = new DateTimeOffset(2026, 12, 19, 21, 15, 0, TimeSpan.Zero);

        Assert.Empty(ReminderScheduleCalculator.Calculate(profile, DueDate, zone, moment.AddMinutes(-1), moment));
        var slot = Assert.Single(ReminderScheduleCalculator.Calculate(profile, DueDate, zone, moment, moment.AddMinutes(1)));
        Assert.Equal(DueDate, slot.Key.LocalDate);
        Assert.Equal(moment, slot.ScheduledAtUtc);
    }

    [Fact]
    public void SpringForward_ShiftsDailyReminderButSkipsNonexistentCriticalSlot()
    {
        // Resolve a nonexistent daily time without inventing an extra hourly repeat.
        var zone = TimeZoneResolver.Resolve("Europe/Berlin");
        var date = new DateOnly(2026, 3, 29);
        var ordinary = new ReminderProfile(ReminderMode.Ordinary, 0, new TimeOnly(2, 30), OverdueMode.Disabled);
        var shifted = Assert.Single(ForDates(ordinary, date, date.AddDays(1), date, zone));
        Assert.Equal(new TimeOnly(3, 0), shifted.LocalTime);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero), shifted.ScheduledAtUtc);
        Assert.Equal(new TimeOnly[] { new(1, 30), new(3, 30) },
            ForDates(Critical(60, new TimeOnly(1, 30), end: new TimeOnly(3, 30)), date, date.AddDays(1), date, zone).Select(x => x.LocalTime));
    }

    [Fact]
    public void FallBack_UsesEarlierOccurrenceOfAmbiguousLocalTimeOnlyOnce()
    {
        // Both occurrences share the same wall-clock identity, so only the earlier one is planned.
        var zone = TimeZoneResolver.Resolve("Europe/Berlin");
        var date = new DateOnly(2026, 10, 25);
        var slots = ForDates(Critical(60, new TimeOnly(1, 30), end: new TimeOnly(3, 30)), date, date.AddDays(1), date, zone);
        var repeated = Assert.Single(slots, x => x.LocalTime == new TimeOnly(2, 30));

        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), repeated.ScheduledAtUtc);
    }

    [Fact]
    public void UnsupportedHorizon_IsRejectedBeforeEnumeratingSlots()
    {
        // Callers must explicitly limit previews of never-ending overdue repeats.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ForDates(Critical(60), DueDate, DueDate.AddYears(2)));
    }

    private static ReminderProfile Critical(int interval, TimeOnly? start = null, OverdueMode overdue = OverdueMode.Daily, TimeOnly? end = null) =>
        new(ReminderMode.Critical, 3, Morning, overdue, interval, start ?? Morning, end ?? new TimeOnly(21, 0));

    private static IReadOnlyList<ScheduledReminder> ForDates(
        ReminderProfile profile, DateOnly start, DateOnly end, DateOnly? dueDate = null, TimeZoneInfo? timeZone = null) =>
        ReminderScheduleCalculator.Calculate(profile, dueDate ?? DueDate, timeZone ?? TimeZoneInfo.Utc,
            new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            new DateTimeOffset(end.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
}
