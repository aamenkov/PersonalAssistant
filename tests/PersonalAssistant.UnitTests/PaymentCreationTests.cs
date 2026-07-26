using PersonalAssistant.Domain;

namespace PersonalAssistant.UnitTests;

public sealed class PaymentCreationTests
{
    [Fact]
    public void Create_StoresNormalizedPaymentData()
    {
        var userId = Guid.NewGuid();
        var payment = RecurringPayment.Create(userId, "  Internet  ", 35, "rub", 1, RecurrenceUnit.Month, new DateOnly(2026, 8, 15), DateTime.UtcNow);

        Assert.Equal(userId, payment.UserId);
        Assert.Equal("Internet", payment.Name);
        Assert.Equal("RUB", payment.Currency);
        Assert.Equal(35, payment.Amount);
        Assert.True(payment.IsActive);
    }

    [Fact]
    public void Create_RejectsNonPositiveAmount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RecurringPayment.Create(
            Guid.NewGuid(), "Internet", 0, "RUB", 1, RecurrenceUnit.Month, new DateOnly(2026, 8, 15), DateTime.UtcNow));
    }
}
