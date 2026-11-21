namespace PersonalAssistant.Domain;

public enum ReminderMode
{
    Ordinary = 0,
    Persistent = 1,
    Critical = 2
}

public enum OverdueMode
{
    Daily = 0,
    Disabled = 1,
    Frequent = 2
}

public sealed record ReminderProfile
{
    public ReminderProfile(ReminderMode mode, int daysBefore, TimeOnly reminderTimeLocal, OverdueMode overdueMode,
        int? criticalIntervalMinutes = null, TimeOnly? criticalWindowStart = null, TimeOnly? criticalWindowEnd = null)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (daysBefore < (mode == ReminderMode.Ordinary ? 0 : 1) || daysBefore > 30)
            throw new ArgumentOutOfRangeException(nameof(daysBefore));
        if (!Enum.IsDefined(overdueMode) || (overdueMode == OverdueMode.Frequent && mode != ReminderMode.Critical))
            throw new ArgumentOutOfRangeException(nameof(overdueMode));

        if (mode == ReminderMode.Critical)
        {
            if (criticalIntervalMinutes is not (60 or 120 or 180))
                throw new ArgumentOutOfRangeException(nameof(criticalIntervalMinutes));
            if (criticalWindowStart is not { } start || criticalWindowEnd is not { } end || start >= end ||
                start.Ticks % TimeSpan.TicksPerMinute != 0 || end.Ticks % TimeSpan.TicksPerMinute != 0)
                throw new ArgumentException("The critical window must have start and end times in the same day, in whole minutes.");
        }
        else if (criticalIntervalMinutes.HasValue || criticalWindowStart.HasValue || criticalWindowEnd.HasValue)
        {
            throw new ArgumentException("Only the critical mode supports a critical window.");
        }

        Mode = mode;
        DaysBefore = daysBefore;
        ReminderTimeLocal = reminderTimeLocal;
        OverdueMode = overdueMode;
        CriticalIntervalMinutes = criticalIntervalMinutes;
        CriticalWindowStart = criticalWindowStart;
        CriticalWindowEnd = criticalWindowEnd;
    }

    public ReminderMode Mode { get; }
    public int DaysBefore { get; }
    public TimeOnly ReminderTimeLocal { get; }
    public OverdueMode OverdueMode { get; }
    public int? CriticalIntervalMinutes { get; }
    public TimeOnly? CriticalWindowStart { get; }
    public TimeOnly? CriticalWindowEnd { get; }

    public static ReminderProfile DefaultFor(ReminderMode mode) =>
        mode == ReminderMode.Critical
            ? new ReminderProfile(mode, 3, new TimeOnly(9, 0), OverdueMode.Daily, 60, new TimeOnly(9, 0), new TimeOnly(21, 0))
            : new ReminderProfile(mode, 3, new TimeOnly(9, 0), OverdueMode.Daily);
}

public sealed class PaymentReminderSettings
{
    private PaymentReminderSettings() { }

    internal PaymentReminderSettings(Guid recurringPaymentId) => RecurringPaymentId = recurringPaymentId;

    public Guid RecurringPaymentId { get; private set; }
    // An existing row without a profile explicitly disables reminders for this payment.
    public ReminderProfile? Profile { get; private set; }

    internal void SetProfile(ReminderProfile? profile) =>
        Profile = profile is null ? null : profile with { };
}
