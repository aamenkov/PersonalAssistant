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
            Task.FromResult<IReadOnlyList<RecurringPayment>>(Items.Where(x => x.UserId == userId).ToList());

        public Task<IReadOnlyList<PaymentTransaction>> GetTransactionsForOwnerAsync(Guid userId, Guid? paymentId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PaymentTransaction>>(Items.Where(x => x.UserId == userId)
                .SelectMany(x => x.Transactions)
                .Where(x => !paymentId.HasValue || x.RecurringPaymentId == paymentId.Value)
                .ToList());

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> HasTransactionForPeriodAsync(Guid userId, Guid paymentId, string paidPeriod, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
