using PersonalAssistant.Domain;

namespace PersonalAssistant.UnitTests;

public sealed class PaymentDateCalculatorTests
{
    [Fact]
    public void MonthlyPayment_ClampsToLastDayOfShortMonth()
    {
        var result = PaymentDateCalculator.CalculateNext(new DateOnly(2026, 1, 31), 1, RecurrenceUnit.Month);
        Assert.Equal(new DateOnly(2026, 2, 28), result);
    }

    [Fact]
    public void MonthlyPayment_WithOriginalDay31_Restores31stAfterShortMonth()
    {
        var february = PaymentDateCalculator.CalculateNext(
            new DateOnly(2026, 1, 31), 1, RecurrenceUnit.Month, 31, false);
        var march = PaymentDateCalculator.CalculateNext(
            february, 1, RecurrenceUnit.Month, 31, false);

        Assert.Equal(new DateOnly(2026, 2, 28), february);
        Assert.Equal(new DateOnly(2026, 3, 31), march);
    }

    [Fact]
    public void MonthlyPayment_LastDayRule_UsesEndOfEveryMonth()
    {
        var february = PaymentDateCalculator.CalculateNext(
            new DateOnly(2026, 1, 31), 1, RecurrenceUnit.Month, 31, true);
        var march = PaymentDateCalculator.CalculateNext(
            february, 1, RecurrenceUnit.Month, 31, true);

        Assert.Equal(new DateOnly(2026, 2, 28), february);
        Assert.Equal(new DateOnly(2026, 3, 31), march);
    }

    [Fact]
    public void AnnualPayment_ClampsLeapDay()
    {
        var result = PaymentDateCalculator.CalculateNext(new DateOnly(2024, 2, 29), 1, RecurrenceUnit.Year);
        Assert.Equal(new DateOnly(2025, 2, 28), result);
    }

    [Fact]
    public void AnnualPayment_WithLeapDayAnchor_UsesLastValidDayInNonLeapYear()
    {
        var result = PaymentDateCalculator.CalculateNext(
            new DateOnly(2024, 2, 29), 1, RecurrenceUnit.Year, 29, false);

        Assert.Equal(new DateOnly(2025, 2, 28), result);
    }

    [Fact]
    public void WeeklyPayment_AddsConfiguredNumberOfWeeks()
    {
        var result = PaymentDateCalculator.CalculateNext(new DateOnly(2026, 7, 15), 2, RecurrenceUnit.Week);
        Assert.Equal(new DateOnly(2026, 7, 29), result);
    }

    [Fact]
    public void OneTimePayment_DoesNotCreateNextDate()
    {
        var date = new DateOnly(2026, 7, 15);
        Assert.Equal(date, PaymentDateCalculator.CalculateNext(date, 1, RecurrenceUnit.Once));
    }
}
