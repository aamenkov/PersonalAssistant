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

    [Fact]
    public async Task GetUpcomingAsync_PrioritizesOverdueAndAddsNextPaymentOutsideWindow()
    {
        var repository = new InMemoryPaymentRepository();
        var userId = Guid.NewGuid();
        repository.Items.Add(RecurringPayment.Create(userId, "Внутри окна", 100, "RUB", 1, RecurrenceUnit.Month,
            new DateOnly(2026, 8, 16), DateTime.UtcNow));
        repository.Items.Add(RecurringPayment.Create(userId, "Просрочен", 200, "RUB", 1, RecurrenceUnit.Month,
            new DateOnly(2026, 8, 10), DateTime.UtcNow));
        repository.Items.Add(RecurringPayment.Create(userId, "Следующий", 300, "RUB", 1, RecurrenceUnit.Month,
            new DateOnly(2026, 9, 12), DateTime.UtcNow));

        var result = await new PaymentService(repository).GetUpcomingAsync(
            userId, new DateOnly(2026, 8, 14), 6, CancellationToken.None);

        Assert.Equal(["Просрочен", "Внутри окна", "Следующий"], result.Select(x => x.Name));
        Assert.True(result[0].IsOverdue);
        Assert.Equal(29, result[2].DaysFromToday);
    }

    [Fact]
    public async Task GetActiveAsync_OrdersPaymentsByNearestDueDate()
    {
        var repository = new InMemoryPaymentRepository();
        var userId = Guid.NewGuid();
        repository.Items.Add(RecurringPayment.Create(userId, "Позже", 100, "RUB", 1, RecurrenceUnit.Month,
            new DateOnly(2026, 9, 20), DateTime.UtcNow));
        repository.Items.Add(RecurringPayment.Create(userId, "Раньше", 100, "RUB", 1, RecurrenceUnit.Month,
            new DateOnly(2026, 8, 20), DateTime.UtcNow));

        var result = await new PaymentService(repository).GetActiveAsync(userId, null, null, CancellationToken.None);

        Assert.Equal(["Раньше", "Позже"], result.Select(x => x.Name));
    }

    [Fact]
    public async Task ReminderService_ClaimsDueReminderOnlyOnce()
    {
        var payments = new InMemoryPaymentRepository();
        payments.ReminderCandidates.Add(new ReminderCandidate(
            Guid.NewGuid(), Guid.NewGuid(), 123, "Internet", 400, "RUB",
            new DateOnly(2026, 8, 17), false, "UTC", new TimeOnly(9), 3));
        var reminderRepository = new InMemoryReminderRepository();
        var service = new ReminderService(payments, reminderRepository);

        var first = await service.GetDueAsync(new DateTime(2026, 8, 14, 9, 1, 0, DateTimeKind.Utc), CancellationToken.None);
        var second = await service.GetDueAsync(new DateTime(2026, 8, 14, 9, 2, 0, DateTimeKind.Utc), CancellationToken.None);

        Assert.Single(first);
        Assert.Equal(ReminderKind.BeforeDue, first[0].Kind);
        Assert.Empty(second);
    }

    private sealed class InMemoryPaymentRepository : IPaymentRepository
    {
        public List<RecurringPayment> Items { get; } = [];
        public List<ReminderCandidate> ReminderCandidates { get; } = [];
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

        public Task AddTransactionAsync(PaymentTransaction transaction, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<ReminderCandidate>> GetReminderCandidatesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ReminderCandidate>>(ReminderCandidates);

        public Task<bool> HasTransactionForPeriodAsync(Guid userId, Guid paymentId, string paidPeriod, CancellationToken cancellationToken) =>
            Task.FromResult(PersistedPeriods.Contains((paymentId, paidPeriod)));
    }

    private sealed class InMemoryReminderRepository : IReminderRepository
    {
        private readonly HashSet<(Guid PaymentId, DateOnly DueDate, DateOnly LocalDate, ReminderKind Kind)> claims = [];

        public Task<bool> TryClaimAsync(Guid paymentId, DateOnly dueDate, DateOnly localDate, ReminderKind kind, DateTime claimedAtUtc, CancellationToken cancellationToken) =>
            Task.FromResult(claims.Add((paymentId, dueDate, localDate, kind)));
    }
}
