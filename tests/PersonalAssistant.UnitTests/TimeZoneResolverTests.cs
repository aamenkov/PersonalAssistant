using PersonalAssistant.Domain;

namespace PersonalAssistant.UnitTests;

public sealed class TimeZoneResolverTests
{
    [Fact]
    public void ResolvesMoscowFixedOffset()
    {
        var zone = TimeZoneResolver.Resolve("UTC+03:00");

        Assert.Equal(TimeSpan.FromHours(3), zone.GetUtcOffset(DateTime.UtcNow));
    }

    [Theory]
    [InlineData("UTC+03:00", 3)]
    [InlineData("UTC+05:00", 5)]
    [InlineData("UTC+12:00", 12)]
    public void ParsesRussianOffsets(string value, int hours)
    {
        Assert.True(TimeZoneResolver.TryParseFixedOffset(value, out var offset));
        Assert.Equal(TimeSpan.FromHours(hours), offset);
    }

    [Fact]
    public void KeepsNamedTimeZonesSupported()
    {
        var zone = TimeZoneResolver.Resolve("Europe/Moscow");

        Assert.Equal("Europe/Moscow", zone.Id);
    }
}
