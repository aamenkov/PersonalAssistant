using PersonalAssistant.Application;
using PersonalAssistant.Domain;

namespace PersonalAssistant.UnitTests;

public sealed class PrelaunchAuditTests
{
    [Theory]
    [InlineData("30.50", 30.50)]
    [InlineData("30,50", 30.50)]
    [InlineData(" 10 ", 10)]
    public void AmountParser_AcceptsSupportedDecimalSeparators(string input, decimal expected)
    {
        var parsed = UserInputParser.TryParsePositiveAmount(input, out var amount);

        Assert.True(parsed);
        Assert.Equal(expected, amount);
    }

    [Theory]
    [InlineData("30,000.50")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void AmountParser_RejectsAmbiguousOrNonPositiveInput(string input)
    {
        Assert.False(UserInputParser.TryParsePositiveAmount(input, out _));
    }

    [Theory]
    [InlineData("2026-08", 2026, 8)]
    [InlineData("2024-02", 2024, 2)]
    public void YearMonthParser_AcceptsStrictFormat(string input, int expectedYear, int expectedMonth)
    {
        var parsed = UserInputParser.TryParseYearMonth(input, out var year, out var month);

        Assert.True(parsed);
        Assert.Equal(expectedYear, year);
        Assert.Equal(expectedMonth, month);
    }

    [Theory]
    [InlineData("08-2026")]
    [InlineData("2026-13")]
    [InlineData("2026-8")]
    [InlineData("text")]
    public void YearMonthParser_RejectsInvalidFormat(string input)
    {
        Assert.False(UserInputParser.TryParseYearMonth(input, out _, out _));
    }

    [Fact]
    public void ConversationReset_ChangesKindAndIncrementsVersion()
    {
        var state = ConversationState.Create(Guid.NewGuid(), ConversationKind.AddPayment, "{}", DateTime.UtcNow);

        state.Reset(ConversationKind.RecordPayment, """{"step":1}""", DateTime.UtcNow);

        Assert.Equal(ConversationKind.RecordPayment, state.Kind);
        Assert.Equal("""{"step":1}""", state.PayloadJson);
        Assert.Equal(2, state.Version);
    }

    [Fact]
    public async Task StartingAddConversation_ReplacesPreviousConversationKind()
    {
        var userId = Guid.NewGuid();
        var state = ConversationState.Create(userId, ConversationKind.RecordPayment, "{}", DateTime.UtcNow);
        var states = new InMemoryConversationStateRepository(state);
        var service = new PaymentConversationService(states, new PaymentService(new InMemoryPaymentRepository()));

        await service.BeginAsync(userId, "RUB", new DateOnly(2026, 8, 13), CancellationToken.None);

        Assert.Equal(ConversationKind.AddPayment, state.Kind);
    }

    [Fact]
    public async Task StartingPaymentRecord_UsesProvidedLocalDate()
    {
        var userId = Guid.NewGuid();
        var payment = RecurringPayment.Create(userId, "Internet", 30, "RUB", 1, RecurrenceUnit.Month,
            new DateOnly(2026, 8, 15), DateTime.UtcNow);
        var payments = new InMemoryPaymentRepository();
        payments.Items.Add(payment);
        var states = new InMemoryConversationStateRepository();
        var service = new PaymentRecordConversationService(states, new PaymentService(payments));

        await service.BeginAsync(userId, payment.Id, new DateOnly(2026, 8, 14), CancellationToken.None);

        Assert.NotNull(states.State);
        Assert.Equal(ConversationKind.RecordPayment, states.State.Kind);
        Assert.Contains("2026-08-14", states.State.PayloadJson);
    }

    private sealed class InMemoryConversationStateRepository(ConversationState? state = null) : IConversationStateRepository
    {
        public ConversationState? State { get; private set; } = state;

        public Task<ConversationState?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(State?.UserId == userId ? State : null);

        public Task AddAsync(ConversationState state, CancellationToken cancellationToken)
        {
            State = state;
            return Task.CompletedTask;
        }

        public void Remove(ConversationState state) => State = null;

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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
            Task.FromResult<IReadOnlyList<PaymentTransaction>>([]);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> HasTransactionForPeriodAsync(Guid userId, Guid paymentId, string paidPeriod, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
