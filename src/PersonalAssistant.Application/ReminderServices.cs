using PersonalAssistant.Domain;

namespace PersonalAssistant.Application;

public sealed record ReminderCandidate(
    Guid PaymentId,
    Guid UserId,
    long ChatId,
    string Name,
    decimal Amount,
    string Currency,
    DateOnly DueDate,
    bool IsAutoDebit,
    string TimeZoneId,
    TimeOnly ReminderTimeLocal,
    int ReminderDaysBefore);

public sealed record ReminderNotification(
    Guid PaymentId,
    long ChatId,
    string Name,
    decimal Amount,
    string Currency,
    DateOnly DueDate,
    ReminderKind Kind,
    int DaysUntilDue,
    bool IsAutoDebit);

public interface IReminderRepository
{
    Task<bool> TryClaimAsync(Guid paymentId, DateOnly dueDate, DateOnly localDate, ReminderKind kind, DateTime claimedAtUtc, CancellationToken cancellationToken);
    Task<bool> HasActiveSnoozeAsync(Guid paymentId, DateOnly dueDate, DateTime utcNow, CancellationToken cancellationToken);
    Task UpsertSnoozeAsync(Guid paymentId, DateOnly dueDate, DateTime snoozedUntilUtc, DateTime createdAtUtc, CancellationToken cancellationToken);
    Task<IReadOnlyList<SnoozedReminderCandidate>> GetDueSnoozesAsync(DateTime utcNow, CancellationToken cancellationToken);
    Task<bool> TryConsumeSnoozeAsync(Guid snoozeId, DateTime consumedAtUtc, CancellationToken cancellationToken);
}

public sealed class ReminderService(
    IPaymentRepository payments,
    IReminderRepository reminders)
{
    public async Task<IReadOnlyList<ReminderNotification>> GetDueAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        var candidates = await payments.GetReminderCandidatesAsync(cancellationToken);
        var result = new List<ReminderNotification>();

        foreach (var candidate in candidates)
        {
            TimeZoneInfo timeZone;
            try
            {
                timeZone = TimeZoneResolver.Resolve(candidate.TimeZoneId);
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                continue;
            }

            var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);
            var localDate = DateOnly.FromDateTime(localNow);
            if (localNow.TimeOfDay < candidate.ReminderTimeLocal.ToTimeSpan())
                continue;

            var daysUntilDue = candidate.DueDate.DayNumber - localDate.DayNumber;
            var kind = daysUntilDue < 0
                ? ReminderKind.Overdue
                : daysUntilDue == 0
                    ? ReminderKind.DueToday
                    : daysUntilDue == candidate.ReminderDaysBefore
                        ? ReminderKind.BeforeDue
                        : (ReminderKind?)null;
            if (!kind.HasValue)
                continue;

            if (await reminders.HasActiveSnoozeAsync(candidate.PaymentId, candidate.DueDate, utcNow, cancellationToken))
                continue;

            if (!await reminders.TryClaimAsync(candidate.PaymentId, candidate.DueDate, localDate, kind.Value, utcNow, cancellationToken))
                continue;

            result.Add(new ReminderNotification(candidate.PaymentId, candidate.ChatId, candidate.Name, candidate.Amount,
                candidate.Currency, candidate.DueDate, kind.Value, daysUntilDue, candidate.IsAutoDebit));
        }

        foreach (var snooze in await reminders.GetDueSnoozesAsync(utcNow, cancellationToken))
        {
            if (!await reminders.TryConsumeSnoozeAsync(snooze.SnoozeId, utcNow, cancellationToken))
                continue;

            result.Add(new ReminderNotification(snooze.PaymentId, snooze.ChatId, snooze.Name, snooze.Amount,
                snooze.Currency, snooze.DueDate, ReminderKind.Snoozed, snooze.DaysUntilDue, snooze.IsAutoDebit));
        }

        return result;
    }
}
