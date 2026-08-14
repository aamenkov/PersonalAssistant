using PersonalAssistant.Application;
using PersonalAssistant.Domain;

namespace PersonalAssistant.UnitTests;

public sealed class PresentationRulesTests
{
    [Theory]
    [InlineData(1, RecurrenceUnit.Month, "каждый месяц")]
    [InlineData(3, RecurrenceUnit.Month, "каждые 3 месяца")]
    [InlineData(1, RecurrenceUnit.Year, "каждый год")]
    [InlineData(1, RecurrenceUnit.Once, "разовый платеж")]
    public void Recurrence_IsHumanReadable(int interval, RecurrenceUnit unit, string expected)
    {
        Assert.Equal(expected, PaymentDisplayNames.Recurrence(interval, unit));
    }

    [Fact]
    public void DateShortcut_SupportsOneWeek()
    {
        Assert.True(DateShortcutCalculator.TryParse("Через неделю", new DateOnly(2026, 8, 15), out var result));
        Assert.Equal(new DateOnly(2026, 8, 22), result);
    }
}
