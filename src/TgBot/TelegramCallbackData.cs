using System.Globalization;

namespace PersonalAssistant.Bot;

internal static class TelegramCallbackData
{
    public const string PaymentPrefix = "payment:";
    public const string TimeZonePrefix = "timezone:";
    public const string SettingsTimeZonePrefix = TimeZonePrefix + "settings:";
    public const string SettingsPrefix = "settings:";
    public const string EditPrefix = "edit:";
    public const string ReminderPrefix = "reminder:";
    public const string PeriodPrefix = "r1:";
    public const string ReminderSettingsPrefix = "nr:";
    public const string ReminderDraftPrefix = "nd:";
    public const string AdminPrefix = "admin:";

    public static string Payment(string action, Guid paymentId) => $"{PaymentPrefix}{action}:{paymentId}";
    public static string TimeZone(string timeZoneId, bool fromSettings = false) =>
        $"{(fromSettings ? SettingsTimeZonePrefix : TimeZonePrefix)}{timeZoneId}";
    public static string Setting(string name) => $"{SettingsPrefix}{name}";
    public static string Setting(string name, object value) => $"{SettingsPrefix}{name}:{value}";
    public static string EditField(string field, Guid paymentId) => $"{EditPrefix}{field}:{paymentId}";
    public static string Month(string prefix, int year, int month) => $"{prefix}month:{year:D4}-{month:D2}";
    public static string Period(string action, Guid paymentId, DateOnly dueDate) =>
        $"{PeriodPrefix}{action}:{paymentId:N}:{dueDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";

    public static bool TryReadPeriod(string data, out string action, out Guid paymentId, out DateOnly dueDate)
    {
        action = "";
        paymentId = default;
        dueDate = default;
        var parts = data.Split(':');
        if (parts.Length != 4 || parts[0] != "r1" || parts[1] is not ("p" or "s")
            || !Guid.TryParseExact(parts[2], "N", out paymentId)
            || !DateOnly.TryParseExact(parts[3], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dueDate))
            return false;
        action = parts[1];
        return true;
    }

    public static string ReminderSettings(string action, Guid? id = null) =>
        id.HasValue ? $"{ReminderSettingsPrefix}{action}:{id:N}" : $"{ReminderSettingsPrefix}{action}";
    public static string ReminderDraft(string nonce, string action) => $"{ReminderDraftPrefix}{nonce}:{action}";
    public static string Admin(string action) => $"{AdminPrefix}{action}";
}
