using PersonalAssistant.Application;
using PersonalAssistant.Domain;

namespace PersonalAssistant.UnitTests;

public sealed class PaymentDisplayNamesTests
{
    [Theory]
    [InlineData(RecurrenceUnit.Week, "еженедельно")]
    [InlineData(RecurrenceUnit.Month, "ежемесячно")]
    [InlineData(RecurrenceUnit.Year, "ежегодно")]
    [InlineData(RecurrenceUnit.Once, "однократно")]
    public void Recurrence_IsDisplayedInRussian(RecurrenceUnit unit, string expected)
    {
        Assert.Equal(expected, PaymentDisplayNames.Recurrence(unit));
    }

    [Theory]
    [InlineData(PaymentMethod.Card, "карта")]
    [InlineData(PaymentMethod.BankTransfer, "банковский перевод")]
    [InlineData(PaymentMethod.Cash, "наличные")]
    [InlineData(PaymentMethod.Other, "другое")]
    public void PaymentMethod_IsDisplayedInRussian(PaymentMethod method, string expected)
    {
        Assert.Equal(expected, PaymentDisplayNames.PaymentMethod(method));
    }
}
