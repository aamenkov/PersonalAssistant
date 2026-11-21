using PersonalAssistant.Domain;

namespace PersonalAssistant.UnitTests;

public sealed class UserSettingsTests
{
    [Fact]
    public void NewUser_HasRussianDefaults()
    {
        var user = new User(1, 2, "Test", "test", DateTime.UtcNow);

        Assert.Equal("RUB", user.DefaultCurrency);
        Assert.Equal(new TimeOnly(9, 0), user.ReminderTimeLocal);
        Assert.Equal(3, user.ReminderDaysBefore);
    }

    [Fact]
    public void Settings_CanBeChanged()
    {
        var user = new User(1, 2, "Test", "test", DateTime.UtcNow);

        user.SetDefaultCurrency("usd", DateTime.UtcNow);
        user.SetReminderSettings(new TimeOnly(10, 30), 7, DateTime.UtcNow);

        Assert.Equal("USD", user.DefaultCurrency);
        Assert.Equal(new TimeOnly(10, 30), user.ReminderTimeLocal);
        Assert.Equal(7, user.ReminderDaysBefore);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(-1)]
    public void ReminderDays_RejectsOutOfRangeValues(int days)
    {
        var user = new User(1, 2, "Test", "test", DateTime.UtcNow);

        Assert.Throws<ArgumentOutOfRangeException>(() => user.SetReminderSettings(new TimeOnly(9, 0), days, DateTime.UtcNow));
    }

    [Fact]
    public void LegacySettings_PreserveOrdinaryOverduePreferenceAndRejectChangesToCriticalProfile()
    {
        // Old callbacks must not rewrite the chosen mode or its configuration.
        var user = new User(1, 2, null, null, DateTime.UnixEpoch);
        user.SetReminderProfile(new ReminderProfile(ReminderMode.Ordinary, 3, new TimeOnly(9, 0), OverdueMode.Disabled), DateTime.UnixEpoch);
        user.SetReminderSettings(new TimeOnly(12, 0), 0, DateTime.UnixEpoch);
        Assert.Equal(new ReminderProfile(ReminderMode.Ordinary, 0, new TimeOnly(12, 0), OverdueMode.Disabled), user.GetReminderProfile());

        var critical = ReminderProfile.DefaultFor(ReminderMode.Critical);
        user.SetReminderProfile(critical, DateTime.UnixEpoch);
        Assert.Throws<InvalidOperationException>(() => user.SetReminderSettings(new TimeOnly(18, 0), 7, DateTime.UnixEpoch));
        Assert.Equal(critical, user.GetReminderProfile());
    }

    [Fact]
    public void ReminderProfiles_RejectInvalidCombinations()
    {
        // A profile cannot be persisted with incompatible modes, repeat intervals or overnight windows.
        var morning = new TimeOnly(9, 0);
        var evening = new TimeOnly(21, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReminderProfile(ReminderMode.Persistent, 0, morning, OverdueMode.Daily));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReminderProfile(ReminderMode.Ordinary, 3, morning, OverdueMode.Frequent));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReminderProfile(ReminderMode.Critical, 3, morning, OverdueMode.Daily, 90, morning, evening));
        Assert.Throws<ArgumentException>(() => new ReminderProfile(ReminderMode.Critical, 3, morning, OverdueMode.Daily, 60, evening, morning));
        Assert.Throws<ArgumentException>(() => new ReminderProfile(ReminderMode.Critical, 3, morning, OverdueMode.Daily, 60, morning, morning));
        Assert.Throws<ArgumentException>(() => new ReminderProfile(ReminderMode.Ordinary, 3, morning, OverdueMode.Daily, 60, morning, evening));
    }
}
