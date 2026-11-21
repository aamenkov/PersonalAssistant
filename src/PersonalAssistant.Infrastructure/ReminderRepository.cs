using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PersonalAssistant.Application;
using PersonalAssistant.Domain;

namespace PersonalAssistant.Infrastructure;

public sealed class ReminderRepository(PersonalAssistantDbContext db) : IReminderRepository
{
    public async Task<IReadOnlyList<ReminderCandidate>> GetCandidatesAsync(CancellationToken cancellationToken)
    {
        var candidates = await Project(ActivePayments()).ToListAsync(cancellationToken);
        return candidates.OfType<ReminderCandidate>().ToArray();
    }

    public async Task<IReadOnlyList<ReminderDispatch>> FilterReadyAsync(
        IReadOnlyList<ReminderDispatch> dispatches, DateTime utcNow, CancellationToken cancellationToken)
    {
        var ready = new List<ReminderDispatch>();
        foreach (var batch in dispatches.Chunk(256))
        {
            var payments = batch.Select(x => x.Payment.PaymentId).Distinct().ToArray();
            var dueDates = batch.Select(x => x.Key.DueDate).Distinct().ToArray();
            var localDates = batch.Select(x => x.Key.LocalDate).Distinct().ToArray();
            var slotKeys = batch.Select(x => x.SlotKey).Distinct().ToArray();
            var rows = await db.Reminders.AsNoTracking()
                .Where(x => payments.Contains(x.RecurringPaymentId) && dueDates.Contains(x.DueDate)
                    && localDates.Contains(x.LocalDate) && slotKeys.Contains(x.SlotKey))
                .Select(x => new
                {
                    x.RecurringPaymentId,
                    x.DueDate,
                    x.LocalDate,
                    x.Kind,
                    x.SlotKey,
                    x.Status,
                    x.LeaseUntilUtc,
                    x.NextAttemptAtUtc
                })
                .ToListAsync(cancellationToken);
            var states = rows.ToDictionary(x => (x.RecurringPaymentId, x.DueDate, x.LocalDate, x.Kind, x.SlotKey));

            // Filter the already selected latest slot. Never replace it with an older one.
            foreach (var dispatch in batch)
            {
                if (!states.TryGetValue((dispatch.Payment.PaymentId, dispatch.Key.DueDate, dispatch.Key.LocalDate,
                        dispatch.Key.Kind, dispatch.SlotKey), out var row) ||
                    row.Status == ReminderDeliveryStatus.Reserved && row.LeaseUntilUtc <= utcNow ||
                    row.Status == ReminderDeliveryStatus.Failed && row.NextAttemptAtUtc <= utcNow ||
                    dispatch.SnoozeId.HasValue && IsTerminal(row.Status, row.NextAttemptAtUtc))
                    ready.Add(dispatch);
            }
        }

        return ready;
    }

    public async Task MarkExpiredAttemptsUnknownAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        await db.Reminders.Where(x => x.Status == ReminderDeliveryStatus.Sending && x.LeaseUntilUtc <= utcNow)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, ReminderDeliveryStatus.Unknown)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null), cancellationToken);
    }

    public async Task<ReminderReservation?> TryReserveAsync(
        ReminderDispatch dispatch, DateTime utcNow, DateTime leaseUntilUtc, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockPaymentAsync(dispatch.Payment.PaymentId, cancellationToken);
        if (!await IsCurrentAsync(dispatch, cancellationToken) || await IsCoveredByPreviousModeAsync(dispatch, cancellationToken))
            return null;

        var existing = await FindSlot(dispatch).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (existing is null)
        {
            var reminder = Reminder.Reserve(dispatch.Payment.PaymentId, dispatch.Key.DueDate, dispatch.Key.LocalDate,
                dispatch.Key.Kind, dispatch.SlotKey, dispatch.ScheduledAtUtc, utcNow, leaseUntilUtc);
            db.Reminders.Add(reminder);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ReminderReservation(reminder.Id, reminder.ClaimToken!.Value, 0);
            }
            finally
            {
                db.Entry(reminder).State = EntityState.Detached;
            }
        }

        if (IsTerminal(existing.Status, existing.NextAttemptAtUtc))
        {
            // An expired Sending attempt can be found after a crash with its snooze still pending.
            await ConsumeSnoozeAsync(dispatch.SnoozeId, utcNow, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var token = Guid.NewGuid();
        var reserved = await db.Reminders.Where(x => x.Id == existing.Id
                && ((x.Status == ReminderDeliveryStatus.Reserved && x.LeaseUntilUtc <= utcNow)
                    || (x.Status == ReminderDeliveryStatus.Failed && x.NextAttemptAtUtc <= utcNow)))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, ReminderDeliveryStatus.Reserved)
                .SetProperty(x => x.ClaimToken, token)
                .SetProperty(x => x.ClaimedAtUtc, utcNow)
                .SetProperty(x => x.ScheduledAtUtc, dispatch.ScheduledAtUtc)
                .SetProperty(x => x.LeaseUntilUtc, leaseUntilUtc)
                .SetProperty(x => x.NextAttemptAtUtc, (DateTime?)null), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return reserved == 1 ? new ReminderReservation(existing.Id, token, existing.AttemptCount) : null;
    }

    public async Task<bool> TryStartAttemptAsync(ReminderDispatch dispatch, ReminderReservation reservation, DateTime utcNow,
        DateTime leaseUntilUtc, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockPaymentAsync(dispatch.Payment.PaymentId, cancellationToken);
        var held = db.Reminders.Where(x => x.Id == reservation.Id && x.ClaimToken == reservation.Token
            && x.Status == ReminderDeliveryStatus.Reserved && x.LeaseUntilUtc > utcNow);
        if (!await IsCurrentAsync(dispatch, cancellationToken) || await IsCoveredByPreviousModeAsync(dispatch, cancellationToken))
        {
            await held.ExecuteUpdateAsync(x => x.SetProperty(r => r.LeaseUntilUtc, utcNow), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        var updated = await held.ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, ReminderDeliveryStatus.Sending)
            .SetProperty(x => x.LeaseUntilUtc, leaseUntilUtc)
            .SetProperty(x => x.LastAttemptAtUtc, utcNow)
            .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated == 1;
    }

    public async Task CompleteAttemptAsync(ReminderDispatch dispatch, ReminderReservation reservation,
        ReminderSendResult result, DateTime utcNow, DateTime? retryAtUtc, CancellationToken cancellationToken)
    {
        var status = result.Outcome switch
        {
            ReminderSendOutcome.Sent => ReminderDeliveryStatus.Sent,
            ReminderSendOutcome.Rejected => ReminderDeliveryStatus.Failed,
            _ => ReminderDeliveryStatus.Unknown
        };
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockPaymentAsync(dispatch.Payment.PaymentId, cancellationToken);
        var updated = await db.Reminders.Where(x => x.Id == reservation.Id && x.ClaimToken == reservation.Token
                && x.Status == ReminderDeliveryStatus.Sending)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, status)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.NextAttemptAtUtc, retryAtUtc)
                .SetProperty(x => x.SentAtUtc, status == ReminderDeliveryStatus.Sent ? utcNow : (DateTime?)null)
                .SetProperty(x => x.TelegramMessageId, result.MessageId), cancellationToken);
        if (updated == 1 && retryAtUtc is null)
            await ConsumeSnoozeAsync(dispatch.SnoozeId, utcNow, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> UpsertSnoozeAsync(Guid userId, Guid paymentId, DateOnly dueDate, DateTime untilUtc,
        DateTime utcNow, CancellationToken cancellationToken)
    {
        var period = dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockPaymentAsync(paymentId, cancellationToken);
        var payment = ActivePayments().Where(x => x.Id == paymentId && x.UserId == userId && x.NextPaymentDate == dueDate
            && !x.Transactions.Any(t => t.PaidPeriod == period));
        if (await Project(payment).SingleOrDefaultAsync(cancellationToken) is null)
            return false;

        // A fresh ID fences stale reservations and outcomes belonging to the previous Snooze.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ReminderSnoozes" ("Id", "RecurringPaymentId", "DueDate", "SnoozedUntilUtc", "CreatedAtUtc")
            VALUES ({Guid.NewGuid()}, {paymentId}, {dueDate}, {untilUtc}, {utcNow})
            ON CONFLICT ("RecurringPaymentId", "DueDate") DO UPDATE SET
                "Id" = EXCLUDED."Id", "SnoozedUntilUtc" = EXCLUDED."SnoozedUntilUtc",
                "CreatedAtUtc" = EXCLUDED."CreatedAtUtc", "ConsumedAtUtc" = NULL
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private IQueryable<RecurringPayment> ActivePayments() =>
        db.RecurringPayments.AsNoTracking().Where(x => x.IsActive && x.NextPaymentDate.HasValue);

    private IQueryable<ReminderCandidate?> Project(IQueryable<RecurringPayment> payments) =>
        from payment in payments
        join snooze in db.ReminderSnoozes
            on new { PaymentId = payment.Id, DueDate = payment.NextPaymentDate }
            equals new { PaymentId = snooze.RecurringPaymentId, DueDate = (DateOnly?)snooze.DueDate } into snoozes
        from snooze in snoozes.DefaultIfEmpty()
        select ToCandidate(payment.Id, payment.User.TelegramChatId, payment.Name, payment.Amount, payment.Currency,
            payment.NextPaymentDate!.Value, payment.IsAutoDebit, payment.User.TimeZoneId,
            payment.ReminderSettings != null, payment.ReminderSettings!.Profile,
            payment.User.ReminderMode, payment.User.ReminderDaysBefore, payment.User.ReminderTimeLocal, payment.User.ReminderOverdueMode,
            payment.User.CriticalIntervalMinutes, payment.User.CriticalWindowStart, payment.User.CriticalWindowEnd,
            snooze != null ? new ReminderSnoozeSnapshot(snooze.Id, snooze.SnoozedUntilUtc, snooze.ConsumedAtUtc) : null);

    private static ReminderCandidate? ToCandidate(Guid paymentId, long chatId, string name, decimal amount, string currency,
        DateOnly dueDate, bool autoDebit, string timeZoneId, bool hasOverride, ReminderProfile? individual,
        ReminderMode mode, int days, TimeOnly time, OverdueMode overdue, int? interval, TimeOnly? start, TimeOnly? end,
        ReminderSnoozeSnapshot? snooze)
    {
        var profile = hasOverride ? individual : new ReminderProfile(mode, days, time, overdue, interval, start, end);
        return profile is null ? null : new ReminderCandidate(paymentId, chatId, name, amount, currency, dueDate, autoDebit, timeZoneId, profile, snooze);
    }

    private async Task<bool> IsCurrentAsync(ReminderDispatch dispatch, CancellationToken cancellationToken)
    {
        var period = dispatch.Key.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var payment = ActivePayments().Where(x => x.Id == dispatch.Payment.PaymentId && x.NextPaymentDate == dispatch.Key.DueDate
            && !x.Transactions.Any(t => t.PaidPeriod == period));
        return await Project(payment).SingleOrDefaultAsync(cancellationToken) == dispatch.Payment;
    }

    private IQueryable<Reminder> FindDay(ReminderDispatch dispatch) =>
        db.Reminders.Where(x => x.RecurringPaymentId == dispatch.Payment.PaymentId && x.DueDate == dispatch.Key.DueDate
            && x.LocalDate == dispatch.Key.LocalDate && x.Kind == dispatch.Key.Kind);

    private IQueryable<Reminder> FindSlot(ReminderDispatch dispatch) =>
        FindDay(dispatch).Where(x => x.SlotKey == dispatch.SlotKey);

    private static bool IsTerminal(ReminderDeliveryStatus status, DateTime? retryAtUtc) =>
        status is ReminderDeliveryStatus.Unknown or ReminderDeliveryStatus.Sent
        || status == ReminderDeliveryStatus.Failed && retryAtUtc is null;

    private Task<bool> IsCoveredByPreviousModeAsync(ReminderDispatch dispatch, CancellationToken cancellationToken)
    {
        if (dispatch.SnoozeId.HasValue)
            return Task.FromResult(false);

        var sameDay = FindDay(dispatch).Where(x => x.LastAttemptAtUtc != null || x.Status == ReminderDeliveryStatus.HistoricalUnknown);
        if (dispatch.Key.CriticalTime is null)
            return sameDay.AnyAsync(x => x.SlotKey.StartsWith(ReminderDispatch.CriticalPrefix), cancellationToken);

        return sameDay.AnyAsync(x => x.SlotKey == ReminderDispatch.DailySlot
            && (x.LastAttemptAtUtc ?? x.ClaimedAtUtc) >= dispatch.ScheduledAtUtc, cancellationToken);
    }

    private Task LockPaymentAsync(Guid paymentId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({paymentId.ToString("N")}, 0))", cancellationToken);

    private Task ConsumeSnoozeAsync(Guid? snoozeId, DateTime utcNow, CancellationToken cancellationToken) =>
        snoozeId.HasValue
            ? db.ReminderSnoozes.Where(x => x.Id == snoozeId.Value && x.ConsumedAtUtc == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ConsumedAtUtc, utcNow), cancellationToken)
            : Task.CompletedTask;
}
