using PersonalAssistant.Application;

namespace PersonalAssistant.UnitTests;

public sealed class ReminderSnoozeTests
{
    [Fact]
    public void OneHour_UsesUtcInstantWithoutChangingPaymentDate()
    {
        var now = new DateTime(2026, 8, 16, 10, 30, 0, DateTimeKind.Utc);

        var result = ReminderSnoozeCalculator.CalculateUntil(ReminderSnoozeOption.InOneHour, now, "UTC", new TimeOnly(9, 0));

        Assert.Equal(new DateTime(2026, 8, 16, 11, 30, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void ThisEvening_UsesUserLocalTime()
    {
        var now = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

        var result = ReminderSnoozeCalculator.CalculateUntil(ReminderSnoozeOption.ThisEvening, now, "Europe/Moscow", new TimeOnly(9, 0));

        Assert.Equal(new DateTime(2026, 8, 16, 15, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void Tomorrow_UsesUserReminderTimeAndTimezone()
    {
        var now = new DateTime(2026, 8, 16, 20, 0, 0, DateTimeKind.Utc);

        var result = ReminderSnoozeCalculator.CalculateUntil(ReminderSnoozeOption.Tomorrow, now, "Europe/Moscow", new TimeOnly(9, 0));

        Assert.Equal(new DateTime(2026, 8, 17, 6, 0, 0, DateTimeKind.Utc), result);
    }
}
