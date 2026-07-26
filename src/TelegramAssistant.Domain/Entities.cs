namespace TelegramAssistant.Domain;

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
        ReminderTimeUtc = TimeOnly.FromTimeSpan(TimeSpan.FromHours(9));
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
    public TimeOnly ReminderTimeUtc { get; private set; }
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
}

public sealed class RecurringPayment
{
    private RecurringPayment() { }

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
    public PaymentMethod PaymentMethod { get; private set; }
    public bool IsAutoDebit { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public ICollection<PaymentTransaction> Transactions { get; private set; } = new List<PaymentTransaction>();
    public ICollection<Reminder> Reminders { get; private set; } = new List<Reminder>();
}

public sealed class PaymentTransaction
{
    private PaymentTransaction() { }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid RecurringPaymentId { get; private set; }
    public RecurringPayment RecurringPayment { get; private set; } = null!;
    public decimal PaidAmount { get; private set; }
    public DateOnly PaidDate { get; private set; }
    public string PaidPeriod { get; private set; } = string.Empty;
    public string? Comment { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
}

public sealed class Reminder
{
    private Reminder() { }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid RecurringPaymentId { get; private set; }
    public RecurringPayment RecurringPayment { get; private set; } = null!;
    public DateOnly DueDate { get; private set; }
    public DateOnly LocalDate { get; private set; }
    public ReminderKind Kind { get; private set; }
    public DateTime SentAtUtc { get; private set; }
}

public sealed class ConversationState
{
    private ConversationState() { }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid UserId { get; private set; }
    public User User { get; private set; } = null!;
    public ConversationKind Kind { get; private set; }
    public string PayloadJson { get; private set; } = "{}";
    public int Version { get; private set; } = 1;
    public DateTime UpdatedAtUtc { get; private set; }
}
