using System.Text;
using PersonalAssistant.Bot;
using PersonalAssistant.Domain;

namespace PersonalAssistant.UnitTests;

public sealed class ReminderTelegramUiTests
{
    [Fact]
    public void Editor_AppliesNewModePresetWithoutResettingTheCurrentMode()
    {
        // A stale click on the current mode must not silently throw away custom days and time.
        var original = new ReminderProfile(ReminderMode.Ordinary, 0, new(7, 45), OverdueMode.Disabled);
        Assert.True(ReminderTelegramHandler.TryChangeField(original, "mode", "0", out var same));
        Assert.Equal(original, same);
        Assert.True(ReminderTelegramHandler.TryChangeField(original, "mode", "2", out var critical));
        Assert.Equal(ReminderProfile.DefaultFor(ReminderMode.Critical), critical);
        Assert.False(ReminderTelegramHandler.TryChangeField(critical, "days", "0", out _));
        Assert.False(ReminderTelegramHandler.TryChangeField(critical, "interval", "90", out _));
        Assert.False(ReminderTelegramHandler.TryChangeField(original, "overdue", "2", out _));
    }

    [Fact]
    public void Preview_ShowsActualFutureSlotsAndSplitsAroundClockChanges()
    {
        // A DST gap must not be rendered as an uninterrupted hourly sequence.
        var due = new DateOnly(2026, 3, 29);
        var now = new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero);
        var profile = new ReminderProfile(ReminderMode.Critical, 1, new(9, 0), OverdueMode.Disabled, 60, new(0, 0), new(4, 0));

        var preview = ReminderTelegramUi.FormatPreview(profile, "Europe/Berlin", now, "Связь", due, false);
        Assert.Contains("29 марта — 01:00; 03:00–04:00, каждый час (2)", preview);
        Assert.DoesNotContain("00:00", preview);
        Assert.DoesNotContain("02:00", preview);
        Assert.Contains("после добавления платежа", ReminderTelegramUi.FormatPreview(profile, "UTC", now, null, null, true));
    }

    [Theory]
    [InlineData("p")]
    [InlineData("s")]
    public void Callback_CarriesThePeriodAndFitsTheTelegramLimit(string action)
    {
        // The real callback must fit the byte limit without relying on text in the old notification.
        var id = Guid.NewGuid();
        var dueDate = new DateOnly(2026, 12, 20);
        var callback = TelegramCallbackData.Period(action, id, dueDate);
        Assert.InRange(Encoding.UTF8.GetByteCount(callback), 1, 64);
        Assert.True(TelegramCallbackData.TryReadPeriod(callback, out var readAction, out var readId, out var readDueDate));
        Assert.Equal((action, id, dueDate), (readAction, readId, readDueDate));
        Assert.False(TelegramCallbackData.TryReadPeriod(TelegramCallbackData.Payment("pay", id), out _, out _, out _));
        Assert.False(TelegramCallbackData.TryReadPeriod($"r1:{action}:{id:N}:20260230", out _, out _, out _));
    }
}
