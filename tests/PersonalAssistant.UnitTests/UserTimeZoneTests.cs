using PersonalAssistant.Domain;

namespace PersonalAssistant.UnitTests;

public sealed class UserTimeZoneTests
{
    [Fact]
    public void NewUser_DoesNotHaveConfiguredTimeZone()
    {
        var user = new User(1, 2, "Alex", "alex", DateTime.UtcNow);

        Assert.Equal("UTC", user.TimeZoneId);
        Assert.False(user.IsTimeZoneConfigured);
    }

    [Fact]
    public void SetTimeZone_MarksTimeZoneAsConfigured()
    {
        var user = new User(1, 2, "Alex", "alex", DateTime.UtcNow);
        user.SetTimeZone("Europe/Moscow", DateTime.UtcNow);

        Assert.Equal("Europe/Moscow", user.TimeZoneId);
        Assert.True(user.IsTimeZoneConfigured);
    }
}
