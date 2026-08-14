using PersonalAssistant.Application;
using PersonalAssistant.Domain;

namespace PersonalAssistant.UnitTests;

public sealed class PaymentServiceTests
{
    [Fact]
    public async Task CreateAsync_PersistsPaymentForRequestedUser()
    {
        var repository = new InMemoryPaymentRepository();
        var service = new PaymentService(repository);
        var userId = Guid.NewGuid();

        await service.CreateAsync(userId, "Internet", 35, "RUB", 1, RecurrenceUnit.Month, new DateOnly(2026, 8, 15), CancellationToken.None);

        var payment = Assert.Single(repository.Items);
        Assert.Equal(userId, payment.UserId);
        Assert.Equal("Internet", payment.Name);
        Assert.Equal(35, payment.Amount);
    }

    [Fact]
    public async Task RecordPaymentAsync_RecordsPaymentAndAdvancesNextDate()
    {
        var repository = new InMemoryPaymentRepository();
        var userId = Guid.NewGuid();
        var payment = RecurringPayment.Create(userId, "Internet", 35, "RUB", 1, RecurrenceUnit.Month,
            new DateOnly(2026, 8, 15), DateTime.UtcNow);
        repository.Items.Add(payment);

        var result = await new PaymentService(repository).RecordPaymentAsync(
            userId, payment.Id, 35, new DateOnly(2026, 8, 14), null, CancellationToken.None);

        Assert.Equal(PaymentRecordStatus.Recorded, result.Status);
        Assert.Equal(new DateOnly(2026, 9, 15), result.NextPaymentDate);
        Assert.Single(payment.Transactions);
    }

    [Fact]
    public async Task RecordPaymentAsync_WhenPeriodAlreadyRecorded_ReturnsIdempotentResult()
    {
        var repository = new InMemoryPaymentRepository();
        var userId = Guid.NewGuid();
        var payment = RecurringPayment.Create(userId, "Internet", 35, "RUB", 1, RecurrenceUnit.Month,
            new DateOnly(2026, 8, 15), DateTime.UtcNow);
        repository.Items.Add(payment);
        repository.PersistedPeriods.Add((payment.Id, "2026-08-15"));

        var result = await new PaymentService(repository).RecordPaymentAsync(
            userId, payment.Id, 35, new DateOnly(2026, 8, 14), null, CancellationToken.None);

        Assert.Equal(PaymentRecordStatus.AlreadyRecorded, result.Status);
        Assert.Equal(new DateOnly(2026, 8, 15), payment.NextPaymentDate);
        Assert.Empty(payment.Transactions);
    }

    [Fact]
    public async Task RecordPaymentAsync_WhenSaveFailsWithoutPersistedTransaction_ReturnsRetryableConflict()
    {
        var repository = new InMemoryPaymentRepository { ThrowConcurrencyOnSave = true };
        var userId = Guid.NewGuid();
        var payment = RecurringPayment.Create(userId, "Internet", 35, "RUB", 1, RecurrenceUnit.Month,
            new DateOnly(2026, 8, 15), DateTime.UtcNow);
        repository.Items.Add(payment);

        var result = await new PaymentService(repository).RecordPaymentAsync(
            userId, payment.Id, 35, new DateOnly(2026, 8, 14), null, CancellationToken.None);

        Assert.Equal(PaymentRecordStatus.SaveConflict, result.Status);
    }

    private sealed class InMemoryPaymentRepository : IPaymentRepository
    {
        public List<RecurringPayment> Items { get; } = [];
        public HashSet<(Guid PaymentId, string PaidPeriod)> PersistedPeriods { get; } = [];
        public bool ThrowConcurrencyOnSave { get; init; }

        public Task AddAsync(RecurringPayment payment, CancellationToken cancellationToken)
        {
            Items.Add(payment);
            return Task.CompletedTask;
        }

        public Task<RecurringPayment?> FindForOwnerAsync(Guid userId, Guid paymentId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.SingleOrDefault(x => x.UserId == userId && x.Id == paymentId));

        public Task<IReadOnlyList<RecurringPayment>> GetActiveAsync(Guid userId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecurringPayment>>(Items.Where(x => x.UserId == userId).ToList());

        public Task<IReadOnlyList<PaymentTransaction>> GetTransactionsForOwnerAsync(Guid userId, Guid? paymentId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PaymentTransaction>>(Items.Where(x => x.UserId == userId)
                .SelectMany(x => x.Transactions)
                .Where(x => !paymentId.HasValue || x.RecurringPaymentId == paymentId.Value)
                .ToList());

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            if (ThrowConcurrencyOnSave)
                throw new PaymentConcurrencyException(new InvalidOperationException("Simulated stale update."));

            foreach (var transaction in Items.SelectMany(x => x.Transactions))
                PersistedPeriods.Add((transaction.RecurringPaymentId, transaction.PaidPeriod));
            return Task.CompletedTask;
        }

        public Task<bool> HasTransactionForPeriodAsync(Guid userId, Guid paymentId, string paidPeriod, CancellationToken cancellationToken) =>
            Task.FromResult(PersistedPeriods.Contains((paymentId, paidPeriod)));
    }
}
