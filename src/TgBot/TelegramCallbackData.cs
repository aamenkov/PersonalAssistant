namespace PersonalAssistant.Bot;

internal static class TelegramCallbackData
{
    public const string PaymentPrefix = "payment:";
    public const string TimeZonePrefix = "timezone:";
    public const string SettingsPrefix = "settings:";
    public const string EditPrefix = "edit:";
    public const string ReminderPrefix = "reminder:";

    public static string Payment(string action, Guid paymentId) => $"{PaymentPrefix}{action}:{paymentId}";
    public static string TimeZone(string timeZoneId) => $"{TimeZonePrefix}{timeZoneId}";
    public static string Setting(string name) => $"{SettingsPrefix}{name}";
    public static string Setting(string name, object value) => $"{SettingsPrefix}{name}:{value}";
    public static string EditField(string field, Guid paymentId) => $"{EditPrefix}{field}:{paymentId}";
    public static string Month(string prefix, int year, int month) => $"{prefix}month:{year:D4}-{month:D2}";
    public static string ReminderSnooze(Guid paymentId, string? option = null) =>
        option is null ? $"{ReminderPrefix}snooze:{paymentId}" : $"{ReminderPrefix}snooze:{paymentId}:{option}";
}
