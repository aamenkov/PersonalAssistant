using PersonalAssistant.Domain;

namespace PersonalAssistant.Application;

public enum ReminderSnoozeOption
{
    InOneHour,
    ThisEvening,
    Tomorrow
}

public sealed record SnoozedReminderCandidate(
    Guid SnoozeId,
    Guid PaymentId,
    long ChatId,
    string Name,
    decimal Amount,
    string Currency,
    DateOnly DueDate,
    bool IsAutoDebit,
    int DaysUntilDue);

public sealed record ReminderSnoozeResult(bool Succeeded, DateTime SnoozedUntilUtc);

public static class ReminderSnoozeCalculator
{
    public static DateTime CalculateUntil(ReminderSnoozeOption option, DateTime utcNow, string timeZoneId, TimeOnly reminderTimeLocal)
    {
        var timeZone = TimeZoneResolver.Resolve(timeZoneId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);
        var localUntil = option switch
        {
            ReminderSnoozeOption.InOneHour => localNow.AddHours(1),
            ReminderSnoozeOption.ThisEvening => localNow.TimeOfDay < new TimeSpan(18, 0, 0)
                ? localNow.Date.AddHours(18)
                : localNow.Date.AddDays(1).AddHours(18),
            ReminderSnoozeOption.Tomorrow => localNow.Date.AddDays(1).Add(reminderTimeLocal.ToTimeSpan()),
            _ => throw new ArgumentOutOfRangeException(nameof(option))
        };
        return TimeZoneInfo.ConvertTimeToUtc(localUntil, timeZone);
    }
}

public sealed class ReminderSnoozeService(
    IPaymentRepository payments,
    IReminderRepository snoozes)
{
    public async Task<ReminderSnoozeResult> SnoozeAsync(
        User user,
        Guid paymentId,
        ReminderSnoozeOption option,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var payment = await payments.FindForOwnerAsync(user.Id, paymentId, cancellationToken);
        if (payment is null || !payment.IsActive || !payment.NextPaymentDate.HasValue)
            return new ReminderSnoozeResult(false, utcNow);

        var untilUtc = ReminderSnoozeCalculator.CalculateUntil(option, utcNow, user.TimeZoneId, user.ReminderTimeLocal);
        await snoozes.UpsertSnoozeAsync(payment.Id, payment.NextPaymentDate.Value, untilUtc, utcNow, cancellationToken);
        return new ReminderSnoozeResult(true, untilUtc);
    }
}
