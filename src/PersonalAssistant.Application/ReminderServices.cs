using System.Globalization;
using PersonalAssistant.Domain;

namespace PersonalAssistant.Application;

public sealed record ReminderSnoozeSnapshot(Guid Id, DateTime UntilUtc, DateTime? ConsumedAtUtc);

public sealed record ReminderCandidate(
    Guid PaymentId, long ChatId, string Name, decimal Amount, string Currency,
    DateOnly DueDate, bool IsAutoDebit, string TimeZoneId, ReminderProfile Profile,
    ReminderSnoozeSnapshot? Snooze);

public sealed record ReminderDispatch(
    ReminderCandidate Payment, ReminderSlotKey Key, DateTime ScheduledAtUtc, int DaysUntilDue, Guid? SnoozeId = null)
{
    public const string DailySlot = "daily";
    public const string CriticalPrefix = "time:";

    public string SlotKey => SnoozeId is { } snoozeId
        ? $"snooze:{snoozeId:N}"
        : Key.CriticalTime is { } time ? CriticalPrefix + time.ToString("HH:mm", CultureInfo.InvariantCulture) : DailySlot;
}

public sealed record ReminderReservation(Guid Id, Guid Token, int PreviousAttempts);

public enum ReminderSendOutcome
{
    Sent,
    Rejected,
    Unknown
}

public sealed record ReminderSendResult(ReminderSendOutcome Outcome, int? MessageId = null, TimeSpan? RetryAfter = null);

public interface IReminderRepository
{
    Task<IReadOnlyList<ReminderCandidate>> GetCandidatesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ReminderDispatch>> FilterReadyAsync(IReadOnlyList<ReminderDispatch> dispatches, DateTime utcNow,
        CancellationToken cancellationToken);
    Task MarkExpiredAttemptsUnknownAsync(DateTime utcNow, CancellationToken cancellationToken);
    Task<ReminderReservation?> TryReserveAsync(ReminderDispatch dispatch, DateTime utcNow, DateTime leaseUntilUtc, CancellationToken cancellationToken);
    Task<bool> TryStartAttemptAsync(ReminderDispatch dispatch, ReminderReservation reservation, DateTime utcNow, DateTime leaseUntilUtc, CancellationToken cancellationToken);
    Task CompleteAttemptAsync(ReminderDispatch dispatch, ReminderReservation reservation, ReminderSendResult result,
        DateTime utcNow, DateTime? retryAtUtc, CancellationToken cancellationToken);
    Task<bool> UpsertSnoozeAsync(Guid userId, Guid paymentId, DateOnly dueDate, DateTime untilUtc, DateTime utcNow, CancellationToken cancellationToken);
}

public sealed class ReminderService(IReminderRepository reminders, TimeProvider timeProvider)
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan ReservationLease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SendLease = TimeSpan.FromMinutes(2);

    public async Task<IReadOnlyList<ReminderDispatch>> GetDueAsync(CancellationToken cancellationToken)
    {
        await reminders.MarkExpiredAttemptsUnknownAsync(timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        var candidates = await reminders.GetCandidatesAsync(cancellationToken);
        var utcNow = timeProvider.GetUtcNow();
        var result = new List<ReminderDispatch>();

        foreach (var payment in candidates)
        {
            if (SelectDue(payment, utcNow) is { } dispatch)
                result.Add(dispatch);
        }

        return await reminders.FilterReadyAsync(result, utcNow.UtcDateTime, cancellationToken);
    }

    public async Task<ReminderReservation?> TryBeginAsync(ReminderDispatch dispatch, CancellationToken cancellationToken)
    {
        var utcNow = timeProvider.GetUtcNow();
        if (SelectDue(dispatch.Payment, utcNow) != dispatch)
            return null;

        var reservation = await reminders.TryReserveAsync(dispatch, utcNow.UtcDateTime,
            (utcNow + ReservationLease).UtcDateTime, cancellationToken);
        if (reservation is null)
            return null;

        utcNow = timeProvider.GetUtcNow();
        if (SelectDue(dispatch.Payment, utcNow) != dispatch)
            return null;

        return await reminders.TryStartAttemptAsync(dispatch, reservation, utcNow.UtcDateTime,
            (utcNow + SendLease).UtcDateTime, cancellationToken) ? reservation : null;
    }

    public Task CompleteAsync(ReminderDispatch dispatch, ReminderReservation reservation, ReminderSendResult result,
        CancellationToken cancellationToken)
    {
        var utcNow = timeProvider.GetUtcNow();
        DateTime? retryAtUtc = null;
        if (result is { Outcome: ReminderSendOutcome.Rejected, RetryAfter: { } delay }
            && reservation.PreviousAttempts + 1 < MaxAttempts)
        {
            var retryAt = utcNow + (delay < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : delay);
            // A pending snooze must not block the regular schedule during a long rate limit.
            if ((dispatch.SnoozeId is null || retryAt - utcNow <= TimeSpan.FromMinutes(5))
                && SelectDue(dispatch.Payment, retryAt) == dispatch)
                retryAtUtc = retryAt.UtcDateTime;
        }

        return reminders.CompleteAttemptAsync(dispatch, reservation, result, utcNow.UtcDateTime, retryAtUtc, cancellationToken);
    }

    public static ReminderDispatch? SelectDue(ReminderCandidate payment, DateTimeOffset utcNow)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneResolver.Resolve(payment.TimeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return null;
        }

        var localNow = TimeZoneInfo.ConvertTime(utcNow, zone);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var daysUntilDue = payment.DueDate.DayNumber - today.DayNumber;
        if (payment.Snooze is { ConsumedAtUtc: null } pending)
        {
            if (pending.UntilUtc > utcNow.UtcDateTime)
                return null;

            // Use the UTC date of the immutable expiry as the bucket for this one-off key.
            var snoozeKey = new ReminderSlotKey(payment.DueDate, DateOnly.FromDateTime(pending.UntilUtc), ReminderKind.Snoozed, null);
            return new ReminderDispatch(payment, snoozeKey, pending.UntilUtc, daysUntilDue, pending.Id);
        }

        var slots = ReminderScheduleCalculator.Calculate(payment.Profile, payment.DueDate, zone,
            utcNow.AddHours(-26), utcNow.AddTicks(1));
        var latest = slots.LastOrDefault(x => x.Key.LocalDate == today);
        if (latest is null || payment.Snooze?.ConsumedAtUtc >= latest.ScheduledAtUtc.UtcDateTime)
            return null;

        if (latest.Key.CriticalTime.HasValue
            && (localNow.TimeOfDay < payment.Profile.CriticalWindowStart!.Value.ToTimeSpan()
                || localNow.TimeOfDay >= payment.Profile.CriticalWindowEnd!.Value.ToTimeSpan() + TimeSpan.FromMinutes(1)))
            return null;

        return new ReminderDispatch(payment, latest.Key, latest.ScheduledAtUtc.UtcDateTime, daysUntilDue);
    }
}
