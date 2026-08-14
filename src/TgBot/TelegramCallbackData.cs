namespace PersonalAssistant.Bot;

internal static class TelegramCallbackData
{
    public const string PaymentPrefix = "payment:";
    public const string TimeZonePrefix = "timezone:";
    public const string SettingsPrefix = "settings:";

    public static string Payment(string action, Guid paymentId) => $"{PaymentPrefix}{action}:{paymentId}";
    public static string TimeZone(string timeZoneId) => $"{TimeZonePrefix}{timeZoneId}";
    public static string Setting(string name) => $"{SettingsPrefix}{name}";
    public static string Setting(string name, object value) => $"{SettingsPrefix}{name}:{value}";
}
