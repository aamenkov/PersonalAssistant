using PersonalAssistant.Domain;

namespace PersonalAssistant.UnitTests;

public sealed class PaymentEditingTests
{
    [Fact]
    public void UpdateDetails_ChangesCurrentValues()
    {
        var payment = CreatePayment();

        payment.UpdateDetails("Internet", 35, "usd", 2, RecurrenceUnit.Month, new DateOnly(2026, 9, 30),
            PaymentMethod.Card, true, "Updated", DateTime.UtcNow);

        Assert.Equal("Internet", payment.Name);
        Assert.Equal(35, payment.Amount);
        Assert.Equal("USD", payment.Currency);
        Assert.Equal(2, payment.RecurrenceInterval);
        Assert.Equal(new DateOnly(2026, 9, 30), payment.NextPaymentDate);
        Assert.Equal(PaymentMethod.Card, payment.PaymentMethod);
        Assert.True(payment.IsAutoDebit);
        Assert.Equal("Updated", payment.Description);
        Assert.True(payment.IsActive);
    }

    [Fact]
    public void Deactivate_MarksPaymentInactiveWithoutDeletingIt()
    {
        var payment = CreatePayment();

        payment.Deactivate(DateTime.UtcNow);

        Assert.False(payment.IsActive);
        Assert.Equal("Internet", payment.Name);
        Assert.Equal(30, payment.Amount);
    }

    [Fact]
    public void UpdateDetails_RejectsNonPositiveAmount()
    {
        var payment = CreatePayment();

        Assert.Throws<ArgumentOutOfRangeException>(() => payment.UpdateDetails("Internet", 0, "RUB", 1,
            RecurrenceUnit.Month, new DateOnly(2026, 8, 15), PaymentMethod.Other, false, null, DateTime.UtcNow));
    }

    private static RecurringPayment CreatePayment() => RecurringPayment.Create(
        Guid.NewGuid(), "Internet", 30, "RUB", 1, RecurrenceUnit.Month, new DateOnly(2026, 8, 15), DateTime.UtcNow);
}
