namespace PersonalAssistant.Domain;

public enum RecurrenceUnit
{
    Once = 0,
    Week = 1,
    Month = 2,
    Year = 3
}

public enum PaymentMethod
{
    Card = 0,
    BankTransfer = 1,
    Cash = 2,
    Other = 3
}

public enum ReminderKind
{
    BeforeDue = 0,
    DueToday = 1,
    Overdue = 2,
    Snoozed = 3
}

public enum ConversationKind
{
    None = 0,
    AddPayment = 1,
    EditPayment = 2,
    RecordPayment = 3,
    TimeZone = 4
}
