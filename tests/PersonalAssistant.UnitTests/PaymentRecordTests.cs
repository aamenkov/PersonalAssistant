using PersonalAssistant.Domain;

namespace PersonalAssistant.UnitTests;

public sealed class PaymentRecordTests
{
    [Fact]
    public void RecordPayment_CreatesImmutableTransactionAndMovesNextDate()
    {
        var payment = RecurringPayment.Create(Guid.NewGuid(), "Internet", 30, "RUB", 1, RecurrenceUnit.Month,
            new DateOnly(2026, 7, 31), DateTime.UtcNow);

        var transaction = payment.RecordPayment(35, new DateOnly(2026, 7, 30), "2026-07-31", "Оплачено", DateTime.UtcNow);

        Assert.Equal(35, transaction.PaidAmount);
        Assert.Equal(new DateOnly(2026, 7, 30), transaction.PaidDate);
        Assert.Equal("2026-07-31", transaction.PaidPeriod);
        Assert.Single(payment.Transactions);
        Assert.Equal(new DateOnly(2026, 8, 31), payment.NextPaymentDate);
        Assert.True(payment.IsActive);
    }

    [Fact]
    public void RecordPayment_EndsOneTimePayment()
    {
        var payment = RecurringPayment.Create(Guid.NewGuid(), "License", 10, "USD", 1, RecurrenceUnit.Once,
            new DateOnly(2026, 8, 1), DateTime.UtcNow);

        payment.RecordPayment(10, new DateOnly(2026, 8, 1), "2026-08-01", null, DateTime.UtcNow);

        Assert.False(payment.IsActive);
        Assert.Single(payment.Transactions);
    }

    [Fact]
    public void RecordPayment_RejectsNonPositiveAmount()
    {
        var payment = RecurringPayment.Create(Guid.NewGuid(), "License", 10, "USD", 1, RecurrenceUnit.Once,
            new DateOnly(2026, 8, 1), DateTime.UtcNow);

        Assert.Throws<ArgumentOutOfRangeException>(() => payment.RecordPayment(0, new DateOnly(2026, 8, 1), "2026-08-01", null, DateTime.UtcNow));
    }
}
