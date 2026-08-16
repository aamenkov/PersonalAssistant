namespace PersonalAssistant.Domain;

public sealed class User
{
    private User() { }

    public User(long telegramUserId, long telegramChatId, string? firstName, string? username, DateTime createdAtUtc)
    {
        TelegramUserId = telegramUserId;
        TelegramChatId = telegramChatId;
        FirstName = firstName;
        Username = username;
        DefaultCurrency = "RUB";
        TimeZoneId = "UTC";
        ReminderTimeLocal = new TimeOnly(9, 0);
        ReminderDaysBefore = 3;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public long TelegramUserId { get; private set; }
    public long TelegramChatId { get; private set; }
    public string? FirstName { get; private set; }
    public string? Username { get; private set; }
    public string DefaultCurrency { get; private set; } = "RUB";
    public string TimeZoneId { get; private set; } = "UTC";
    public bool IsTimeZoneConfigured { get; private set; }
    public TimeOnly ReminderTimeLocal { get; private set; }
    public int ReminderDaysBefore { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public ICollection<RecurringPayment> Payments { get; private set; } = new List<RecurringPayment>();
    public ConversationState? ConversationState { get; private set; }

    public void UpdateTelegramProfile(long chatId, string? firstName, string? username, DateTime updatedAtUtc)
    {
        TelegramChatId = chatId;
        FirstName = firstName;
        Username = username;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void SetTimeZone(string timeZoneId, DateTime updatedAtUtc)
    {
        TimeZoneId = timeZoneId;
        IsTimeZoneConfigured = true;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void SetDefaultCurrency(string currency, DateTime updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3 || currency.Any(c => !char.IsLetter(c)))
            throw new ArgumentException("Currency must be a three-letter code.", nameof(currency));

        DefaultCurrency = currency.Trim().ToUpperInvariant();
        UpdatedAtUtc = updatedAtUtc;
    }

    public void SetReminderSettings(TimeOnly reminderTimeLocal, int reminderDaysBefore, DateTime updatedAtUtc)
    {
        if (reminderDaysBefore is < 0 or > 30)
            throw new ArgumentOutOfRangeException(nameof(reminderDaysBefore));

        ReminderTimeLocal = reminderTimeLocal;
        ReminderDaysBefore = reminderDaysBefore;
        UpdatedAtUtc = updatedAtUtc;
    }
}

public sealed class RecurringPayment
{
    private RecurringPayment() { }

    public static RecurringPayment Create(
        Guid userId,
        string name,
        decimal amount,
        string currency,
        int recurrenceInterval,
        RecurrenceUnit recurrenceUnit,
        DateOnly nextPaymentDate,
        DateTime createdAtUtc,
        int? scheduleDayOfMonth = null,
        bool isLastDayOfMonth = false)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Payment name is required.", nameof(name));
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Payment amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException("Currency must be a three-letter code.", nameof(currency));
        if (recurrenceInterval <= 0)
            throw new ArgumentOutOfRangeException(nameof(recurrenceInterval));
        if (scheduleDayOfMonth is < 1 or > 31)
            throw new ArgumentOutOfRangeException(nameof(scheduleDayOfMonth));

        return new RecurringPayment
        {
            UserId = userId,
            Name = name.Trim(),
            Amount = amount,
            Currency = currency.Trim().ToUpperInvariant(),
            RecurrenceInterval = recurrenceInterval,
            RecurrenceUnit = recurrenceUnit,
            NextPaymentDate = nextPaymentDate,
            ScheduleDayOfMonth = scheduleDayOfMonth ?? nextPaymentDate.Day,
            IsLastDayOfMonth = isLastDayOfMonth,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc
        };
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid UserId { get; private set; }
    public User User { get; private set; } = null!;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "RUB";
    public int RecurrenceInterval { get; private set; } = 1;
    public RecurrenceUnit RecurrenceUnit { get; private set; } = RecurrenceUnit.Month;
    public DateOnly? NextPaymentDate { get; private set; }
    public int ScheduleDayOfMonth { get; private set; } = 1;
    public bool IsLastDayOfMonth { get; private set; }
    public PaymentMethod PaymentMethod { get; private set; }
    public bool IsAutoDebit { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public ICollection<PaymentTransaction> Transactions { get; private set; } = new List<PaymentTransaction>();
    public ICollection<Reminder> Reminders { get; private set; } = new List<Reminder>();

    public void UpdateDetails(
        string name,
        decimal amount,
        string currency,
        int recurrenceInterval,
        RecurrenceUnit recurrenceUnit,
        DateOnly nextPaymentDate,
        PaymentMethod paymentMethod,
        bool isAutoDebit,
        string? description,
        DateTime updatedAtUtc,
        int? scheduleDayOfMonth = null,
        bool? isLastDayOfMonth = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Payment name is required.", nameof(name));
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Payment amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3)
            throw new ArgumentException("Currency must be a three-letter code.", nameof(currency));
        if (recurrenceInterval <= 0)
            throw new ArgumentOutOfRangeException(nameof(recurrenceInterval));
        if (scheduleDayOfMonth is < 1 or > 31)
            throw new ArgumentOutOfRangeException(nameof(scheduleDayOfMonth));

        Name = name.Trim();
        Amount = amount;
        Currency = currency.Trim().ToUpperInvariant();
        RecurrenceInterval = recurrenceInterval;
        RecurrenceUnit = recurrenceUnit;
        NextPaymentDate = nextPaymentDate;
        ScheduleDayOfMonth = scheduleDayOfMonth ?? nextPaymentDate.Day;
        IsLastDayOfMonth = isLastDayOfMonth ?? IsLastDayOfMonth;
        PaymentMethod = paymentMethod;
        IsAutoDebit = isAutoDebit;
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        UpdatedAtUtc = updatedAtUtc;
    }

    public void Deactivate(DateTime updatedAtUtc)
    {
        IsActive = false;
        UpdatedAtUtc = updatedAtUtc;
    }

    public PaymentTransaction RecordPayment(decimal paidAmount, DateOnly paidDate, string paidPeriod, string? comment, DateTime createdAtUtc)
    {
        if (!IsActive)
            throw new InvalidOperationException("Inactive payment cannot be paid.");
        if (paidAmount <= 0)
            throw new ArgumentOutOfRangeException(nameof(paidAmount), "Paid amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(paidPeriod))
            throw new ArgumentException("Paid period is required.", nameof(paidPeriod));

        var transaction = PaymentTransaction.Create(Id, Amount, paidAmount, Currency, paidDate, paidPeriod, comment, createdAtUtc);
        transaction.RecurringPayment = this;
        Transactions.Add(transaction);
        if (RecurrenceUnit == RecurrenceUnit.Once)
            Deactivate(createdAtUtc);
        else if (NextPaymentDate.HasValue)
        {
            NextPaymentDate = PaymentDateCalculator.CalculateNext(NextPaymentDate.Value, RecurrenceInterval, RecurrenceUnit,
                ScheduleDayOfMonth, IsLastDayOfMonth);
            UpdatedAtUtc = createdAtUtc;
        }

        return transaction;
    }
}

public sealed class PaymentTransaction
{
    private PaymentTransaction() { }

    private PaymentTransaction(Guid recurringPaymentId, decimal expectedAmount, decimal paidAmount, string currency, DateOnly paidDate, string paidPeriod, string? comment, DateTime createdAtUtc)
    {
        RecurringPaymentId = recurringPaymentId;
        ExpectedAmount = expectedAmount;
        PaidAmount = paidAmount;
        Currency = currency;
        PaidDate = paidDate;
        PaidPeriod = paidPeriod.Trim();
        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        CreatedAtUtc = createdAtUtc;
    }

    public static PaymentTransaction Create(Guid recurringPaymentId, decimal expectedAmount, decimal paidAmount, string currency, DateOnly paidDate, string paidPeriod, string? comment, DateTime createdAtUtc) =>
        new(recurringPaymentId, expectedAmount, paidAmount, currency, paidDate, paidPeriod, comment, createdAtUtc);

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid RecurringPaymentId { get; private set; }
    public RecurringPayment RecurringPayment { get; internal set; } = null!;
    public decimal ExpectedAmount { get; private set; }
    public decimal PaidAmount { get; private set; }
    public string Currency { get; private set; } = "RUB";
    public DateOnly PaidDate { get; private set; }
    public string PaidPeriod { get; private set; } = string.Empty;
    public string? Comment { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
}

public sealed class Reminder
{
    private Reminder() { }

    public static Reminder Create(Guid recurringPaymentId, DateOnly dueDate, DateOnly localDate, ReminderKind kind, DateTime sentAtUtc) => new()
    {
        RecurringPaymentId = recurringPaymentId,
        DueDate = dueDate,
        LocalDate = localDate,
        Kind = kind,
        SentAtUtc = sentAtUtc
    };

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid RecurringPaymentId { get; private set; }
    public RecurringPayment RecurringPayment { get; private set; } = null!;
    public DateOnly DueDate { get; private set; }
    public DateOnly LocalDate { get; private set; }
    public ReminderKind Kind { get; private set; }
    public DateTime SentAtUtc { get; private set; }
}

public sealed class ReminderSnooze
{
    private ReminderSnooze() { }

    public static ReminderSnooze Create(Guid recurringPaymentId, DateOnly dueDate, DateTime snoozedUntilUtc, DateTime createdAtUtc) => new()
    {
        RecurringPaymentId = recurringPaymentId,
        DueDate = dueDate,
        SnoozedUntilUtc = snoozedUntilUtc,
        CreatedAtUtc = createdAtUtc
    };

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid RecurringPaymentId { get; private set; }
    public RecurringPayment RecurringPayment { get; private set; } = null!;
    public DateOnly DueDate { get; private set; }
    public DateTime SnoozedUntilUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ConsumedAtUtc { get; private set; }

    public void Consume(DateTime consumedAtUtc) => ConsumedAtUtc = consumedAtUtc;
}

public sealed class ConversationState
{
    private ConversationState() { }

    public static ConversationState Create(Guid userId, ConversationKind kind, string payloadJson, DateTime updatedAtUtc) => new()
    {
        UserId = userId,
        Kind = kind,
        PayloadJson = payloadJson,
        UpdatedAtUtc = updatedAtUtc
    };

    public void UpdatePayload(string payloadJson, DateTime updatedAtUtc)
    {
        PayloadJson = payloadJson;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void Reset(ConversationKind kind, string payloadJson, DateTime updatedAtUtc)
    {
        Kind = kind;
        PayloadJson = payloadJson;
        UpdatedAtUtc = updatedAtUtc;
        Version++;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid UserId { get; private set; }
    public User User { get; private set; } = null!;
    public ConversationKind Kind { get; private set; }
    public string PayloadJson { get; private set; } = "{}";
    public int Version { get; private set; } = 1;
    public DateTime UpdatedAtUtc { get; private set; }
}
