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
}
