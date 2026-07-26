using PersonalAssistant.Application;
using PersonalAssistant.Domain;

namespace PersonalAssistant.UnitTests;

public sealed class MonthlyStatisticsTests
{
    [Fact]
    public async Task Statistics_SeparatesCurrenciesAndIncludesPaidPlan()
    {
        var userId = Guid.NewGuid();
        var repository = new InMemoryPaymentRepository();
        var rub = RecurringPayment.Create(userId, "Internet", 100, "RUB", 1, RecurrenceUnit.Month,
            new DateOnly(2026, 8, 15), DateTime.UtcNow);
        var usd = RecurringPayment.Create(userId, "Service", 20, "USD", 1, RecurrenceUnit.Month,
            new DateOnly(2026, 8, 20), DateTime.UtcNow);
        var inactive = RecurringPayment.Create(userId, "Old", 50, "RUB", 1, RecurrenceUnit.Month,
            new DateOnly(2026, 8, 10), DateTime.UtcNow);
        inactive.Deactivate(DateTime.UtcNow);
        repository.Items.AddRange([rub, usd, inactive]);
        rub.RecordPayment(90, new DateOnly(2026, 8, 15), "2026-08-15", null, DateTime.UtcNow);
        rub.UpdateDetails("Internet", 150, "RUB", 1, RecurrenceUnit.Month, rub.NextPaymentDate!.Value,
            PaymentMethod.Card, false, null, DateTime.UtcNow);

        var statistics = await new PaymentService(repository).GetMonthlyStatisticsAsync(userId, 2026, 8, CancellationToken.None);

        var rubStats = Assert.Single(statistics.Where(x => x.Currency == "RUB"));
        Assert.Equal(100, rubStats.PlannedAmount);
        Assert.Equal(90, rubStats.PaidAmount);
        Assert.Equal(10, rubStats.RemainingAmount);
        Assert.Equal(1, rubStats.PlannedCount);
        Assert.Equal(1, rubStats.PaidCount);
        Assert.Equal(0, rubStats.UnpaidCount);

        var usdStats = Assert.Single(statistics.Where(x => x.Currency == "USD"));
        Assert.Equal(20, usdStats.PlannedAmount);
        Assert.Equal(0, usdStats.PaidAmount);
        Assert.Equal(1, usdStats.UnpaidCount);
        Assert.DoesNotContain(statistics, x => x.PlannedAmount == 50);
    }

    [Fact]
    public async Task Statistics_DoesNotExposeAnotherUsersData()
    {
        var repository = new InMemoryPaymentRepository();
        repository.Items.Add(RecurringPayment.Create(Guid.NewGuid(), "Private", 100, "RUB", 1, RecurrenceUnit.Month,
            new DateOnly(2026, 8, 15), DateTime.UtcNow));

        var statistics = await new PaymentService(repository).GetMonthlyStatisticsAsync(Guid.NewGuid(), 2026, 8, CancellationToken.None);

        Assert.Empty(statistics);
    }

    private sealed class InMemoryPaymentRepository : IPaymentRepository
    {
        public List<RecurringPayment> Items { get; } = [];

        public Task AddAsync(RecurringPayment payment, CancellationToken cancellationToken)
        {
            Items.Add(payment);
            return Task.CompletedTask;
        }

        public Task<RecurringPayment?> FindForOwnerAsync(Guid userId, Guid paymentId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.SingleOrDefault(x => x.UserId == userId && x.Id == paymentId));

        public Task<IReadOnlyList<RecurringPayment>> GetActiveAsync(Guid userId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecurringPayment>>(Items.Where(x => x.UserId == userId && x.IsActive).ToList());

        public Task<IReadOnlyList<PaymentTransaction>> GetTransactionsForOwnerAsync(Guid userId, Guid? paymentId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PaymentTransaction>>(Items.Where(x => x.UserId == userId)
                .SelectMany(x => x.Transactions)
                .Where(x => (!paymentId.HasValue || x.RecurringPaymentId == paymentId.Value) && (!from.HasValue || x.PaidDate >= from.Value) && (!to.HasValue || x.PaidDate <= to.Value))
                .ToList());

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
