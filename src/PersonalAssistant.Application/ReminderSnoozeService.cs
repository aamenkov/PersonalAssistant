using PersonalAssistant.Domain;

namespace PersonalAssistant.Application;

public enum ReminderSnoozeOption
{
    InOneHour,
    ThisEvening,
    Tomorrow,
    InThreeHours,
    TodayAt19,
    TomorrowAt9
}

public sealed record ReminderSnoozeResult(bool Succeeded, DateTime SnoozedUntilUtc);

public static class ReminderSnoozeCalculator
{
    public static DateTime CalculateUntil(ReminderSnoozeOption option, DateTime utcNow, string timeZoneId, TimeOnly reminderTimeLocal)
    {
        if (option == ReminderSnoozeOption.InOneHour)
            return utcNow.AddHours(1);
        if (option == ReminderSnoozeOption.InThreeHours)
            return utcNow.AddHours(3);

        var timeZone = TimeZoneResolver.Resolve(timeZoneId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);
        var localUntil = option switch
        {
            ReminderSnoozeOption.ThisEvening => localNow.TimeOfDay < new TimeSpan(18, 0, 0)
                ? localNow.Date.AddHours(18)
                : localNow.Date.AddDays(1).AddHours(18),
            ReminderSnoozeOption.Tomorrow => localNow.Date.AddDays(1).Add(reminderTimeLocal.ToTimeSpan()),
            ReminderSnoozeOption.TodayAt19 => localNow.Date.AddHours(19),
            ReminderSnoozeOption.TomorrowAt9 => localNow.Date.AddDays(1).AddHours(9),
            _ => throw new ArgumentOutOfRangeException(nameof(option))
        };
        return ResolveLocal(localUntil, timeZone)
            ?? throw new ArgumentException("This local time does not exist in the selected time zone.", nameof(option));
    }

    public static DateTime? ResolveLocal(DateOnly date, TimeOnly time, string timeZoneId) =>
        ResolveLocal(date.ToDateTime(time), TimeZoneResolver.Resolve(timeZoneId));

    private static DateTime? ResolveLocal(DateTime localTime, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(localTime))
            return null;
        if (zone.IsAmbiguousTime(localTime))
            return new DateTimeOffset(localTime, zone.GetAmbiguousTimeOffsets(localTime).Max()).UtcDateTime;
        return TimeZoneInfo.ConvertTimeToUtc(localTime, zone);
    }
}

public sealed class ReminderSnoozeService(
    IPaymentRepository payments,
    IReminderRepository snoozes)
{
    public Task<ReminderSnoozeResult> SnoozeAsync(
        User user,
        Guid paymentId,
        ReminderSnoozeOption option,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var untilUtc = ReminderSnoozeCalculator.CalculateUntil(option, utcNow, user.TimeZoneId, user.ReminderTimeLocal);
        return SnoozeUntilAsync(user, paymentId, untilUtc, utcNow, cancellationToken);
    }

    public Task<ReminderSnoozeResult> SnoozeUntilLocalAsync(User user, Guid paymentId, DateOnly date, TimeOnly time,
        DateTime utcNow, CancellationToken cancellationToken)
    {
        var untilUtc = ReminderSnoozeCalculator.ResolveLocal(date, time, user.TimeZoneId);
        return untilUtc.HasValue
            ? SnoozeUntilAsync(user, paymentId, untilUtc.Value, utcNow, cancellationToken)
            : Task.FromResult(new ReminderSnoozeResult(false, utcNow));
    }

    public async Task<ReminderSnoozeResult> SnoozeUntilAsync(User user, Guid paymentId, DateTime untilUtc,
        DateTime utcNow, CancellationToken cancellationToken)
    {
        if (utcNow.Kind != DateTimeKind.Utc || untilUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Snooze timestamps must be UTC.");
        if (untilUtc <= utcNow)
            return new ReminderSnoozeResult(false, utcNow);

        var payment = await payments.FindForOwnerAsync(user.Id, paymentId, cancellationToken);
        if (payment is null || !payment.IsActive || !payment.NextPaymentDate.HasValue)
            return new ReminderSnoozeResult(false, utcNow);

        return await snoozes.UpsertSnoozeAsync(user.Id, payment.Id, payment.NextPaymentDate.Value, untilUtc, utcNow, cancellationToken)
            ? new ReminderSnoozeResult(true, untilUtc)
            : new ReminderSnoozeResult(false, utcNow);
    }
}
