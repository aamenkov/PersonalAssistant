using PersonalAssistant.Application;

namespace PersonalAssistant.UnitTests;

public sealed class DateShortcutCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 8, 15);

    [Fact]
    public void Shortcuts_ReturnExpectedDates()
    {
        Assert.Equal(new DateOnly(2026, 8, 15), Parse("сегодня"));
        Assert.Equal(new DateOnly(2026, 8, 16), Parse("завтра"));
        Assert.Equal(new DateOnly(2026, 9, 1), Parse("первое число следующего месяца"));
        Assert.Equal(new DateOnly(2026, 9, 15), Parse("то же число следующего месяца"));
    }

    [Fact]
    public void SameDayNextMonth_UsesLastDayWhenTargetMonthIsShorter()
    {
        Assert.Equal(new DateOnly(2026, 2, 28), DateShortcutCalculator.SameDayNextMonth(new DateOnly(2026, 1, 31)));
    }

    private static DateOnly Parse(string input)
    {
        Assert.True(DateShortcutCalculator.TryParse(input, Today, out var date));
        return date;
    }
}
