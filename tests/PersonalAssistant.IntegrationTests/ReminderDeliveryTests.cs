using Microsoft.EntityFrameworkCore;
using PersonalAssistant.Application;
using PersonalAssistant.Domain;
using PersonalAssistant.Infrastructure;

namespace PersonalAssistant.IntegrationTests;

public sealed class ReminderDeliveryTests
{
    private static readonly DateTime Day = new(2026, 12, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly DueDate = DateOnly.FromDateTime(Day);
    private static readonly CancellationToken Token = CancellationToken.None;

    [ReminderProfilePostgresFact]
    public async Task SnoozeExpiry_CoalescesTheScheduledSlotAcrossTwoWorkersAndResumesAtNextSlot()
    {
        // Simultaneous workers must compete for one delivery when a critical slot coincides with snooze expiry.
        var options = ReminderProfileMigrationTests.CreateOptions();
        await using var db = new PersonalAssistantDbContext(options);
        await using var secondDb = new PersonalAssistantDbContext(options);
        await db.Database.MigrateAsync();
        var (user, payment) = await SeedAsync(db, ReminderMode.Critical);
        var other = RecurringPayment.Create(user.Id, "Phone", 50, "RUB", 1, RecurrenceUnit.Month, DueDate, Day);
        db.Add(other);
        await db.SaveChangesAsync();
        var repository = new ReminderRepository(db);
        var snoozes = new ReminderSnoozeService(new PaymentRepository(db), repository);
        var clock = new ManualClock(Day.AddHours(16));
        var worker = new ReminderService(repository, clock);
        var secondWorker = new ReminderService(new ReminderRepository(secondDb), clock);

        var initial = Assert.Single(await worker.GetDueAsync(Token), x => x.Payment.PaymentId == payment.Id);
        var stale = await repository.TryReserveAsync(initial, clock.Now, clock.Now.AddMinutes(2), Token);
        Assert.NotNull(stale);
        Assert.False((await snoozes.SnoozeUntilLocalAsync(user, payment.Id, DueDate, new TimeOnly(15, 0), clock.Now, Token)).Succeeded);
        Assert.False((await snoozes.SnoozeUntilAsync(user, payment.Id, clock.Now, clock.Now, Token)).Succeeded);
        Assert.True((await snoozes.SnoozeUntilAsync(user, payment.Id, Day.AddHours(18), clock.Now, Token)).Succeeded);
        var firstSnooze = await db.ReminderSnoozes.AsNoTracking().SingleAsync();
        Assert.True((await snoozes.SnoozeUntilLocalAsync(user, payment.Id, DueDate, new TimeOnly(19, 0), clock.Now, Token)).Succeeded);
        Assert.NotEqual(firstSnooze.Id, (await db.ReminderSnoozes.AsNoTracking().SingleAsync()).Id);
        Assert.False(await repository.TryStartAttemptAsync(initial, stale, clock.Now, clock.Now.AddMinutes(2), Token));
        Assert.Equal(other.Id, Assert.Single(await worker.GetDueAsync(Token)).Payment.PaymentId);

        clock.Now = Day.AddHours(19);
        var dispatch = Assert.Single(await worker.GetDueAsync(Token), x => x.Payment.PaymentId == payment.Id);
        var otherDispatch = Assert.Single(await secondWorker.GetDueAsync(Token), x => x.Payment.PaymentId == payment.Id);
        Assert.Equal(ReminderKind.Snoozed, dispatch.Key.Kind);
        var attempts = await Task.WhenAll(worker.TryBeginAsync(dispatch, Token), secondWorker.TryBeginAsync(otherDispatch, Token));
        var reservation = Assert.Single(attempts.OfType<ReminderReservation>());
        var winner = attempts[0] is null ? secondWorker : worker;
        await winner.CompleteAsync(dispatch, reservation, new ReminderSendResult(ReminderSendOutcome.Sent, 42), Token);
        Assert.Single(await db.Reminders.AsNoTracking().Where(x => x.Status == ReminderDeliveryStatus.Sent).ToListAsync());
        Assert.Equal(clock.Now, (await db.ReminderSnoozes.AsNoTracking().SingleAsync()).ConsumedAtUtc);

        clock.Now = clock.Now.AddMinutes(1);
        Assert.DoesNotContain(await worker.GetDueAsync(Token), x => x.Payment.PaymentId == payment.Id);
        clock.Now = Day.AddHours(20);
        var resumed = Assert.Single(await worker.GetDueAsync(Token), x => x.Payment.PaymentId == payment.Id);
        Assert.Equal(new TimeOnly(20, 0), resumed.Key.CriticalTime);

        Assert.True((await snoozes.SnoozeUntilAsync(user, payment.Id, Day.AddHours(22), clock.Now, Token)).Succeeded);
        clock.Now = Day.AddHours(22);
        Assert.Equal(ReminderKind.Snoozed, Assert.Single(await worker.GetDueAsync(Token), x => x.Payment.PaymentId == payment.Id).Key.Kind);
    }

    [ReminderProfilePostgresFact]
    public async Task Restart_ReclaimsUnusedReservationButNeverRetriesUncertainSend()
    {
        // Expired reservations can safely be taken over, whereas interrupted HTTP attempts cannot.
        var options = ReminderProfileMigrationTests.CreateOptions();
        await using var db = new PersonalAssistantDbContext(options);
        await using var secondDb = new PersonalAssistantDbContext(options);
        await db.Database.MigrateAsync();
        await SeedAsync(db, ReminderMode.Ordinary);
        var repository = new ReminderRepository(db);
        var clock = new ManualClock(Day.AddHours(10));
        var worker = new ReminderService(repository, clock);
        var replacement = new ReminderService(new ReminderRepository(secondDb), clock);
        var dispatch = Assert.Single(await worker.GetDueAsync(Token));
        var abandoned = await repository.TryReserveAsync(dispatch, clock.Now, clock.Now.AddMinutes(2), Token);
        Assert.NotNull(abandoned);
        Assert.Null(await replacement.TryBeginAsync(dispatch, Token));

        clock.Now = clock.Now.AddMinutes(3);
        var newReservation = await replacement.TryBeginAsync(dispatch, Token);
        Assert.NotNull(newReservation);
        Assert.Equal(abandoned.Id, newReservation.Id);
        Assert.NotEqual(abandoned.Token, newReservation.Token);
        Assert.False(await repository.TryStartAttemptAsync(dispatch, abandoned, clock.Now, clock.Now.AddMinutes(2), Token));

        clock.Now = clock.Now.AddMinutes(3);
        await worker.GetDueAsync(Token);
        Assert.Null(await worker.TryBeginAsync(dispatch, Token));
        var persisted = await db.Reminders.AsNoTracking().SingleAsync();
        Assert.Equal(ReminderDeliveryStatus.Unknown, persisted.Status);
        Assert.Equal(1, persisted.AttemptCount);
        Assert.Null(persisted.SentAtUtc);
        Assert.Null(persisted.NextAttemptAtUtc);
    }

    [ReminderProfilePostgresFact]
    public async Task Retry_HonorsRateLimitCapsAttemptsAndSkipsEarlierCriticalSlots()
    {
        // A recovered worker processes only the latest slot and can attempt it at most three times.
        await using var db = new PersonalAssistantDbContext(ReminderProfileMigrationTests.CreateOptions());
        await db.Database.MigrateAsync();
        await SeedAsync(db, ReminderMode.Critical);
        var clock = new ManualClock(Day.AddHours(16).AddMinutes(30));
        var worker = new ReminderService(new ReminderRepository(db), clock);
        var dispatch = Assert.Single(await worker.GetDueAsync(Token));
        Assert.Equal(new TimeOnly(16, 0), dispatch.Key.CriticalTime);
        var rejected = new ReminderSendResult(ReminderSendOutcome.Rejected, RetryAfter: TimeSpan.FromMinutes(2));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var reservation = await worker.TryBeginAsync(dispatch, Token);
            Assert.NotNull(reservation);
            await worker.CompleteAsync(dispatch, reservation, rejected, Token);
            clock.Now = clock.Now.AddMinutes(1);
            Assert.Null(await worker.TryBeginAsync(dispatch, Token));
            clock.Now = clock.Now.AddMinutes(1);
        }

        Assert.Empty(await worker.GetDueAsync(Token));
        var failed = await db.Reminders.AsNoTracking().SingleAsync();
        Assert.Equal(3, failed.AttemptCount);
        Assert.Equal(ReminderDeliveryStatus.Failed, failed.Status);
        Assert.Null(failed.NextAttemptAtUtc);

        clock.Now = Day.AddHours(17).AddMinutes(59);
        dispatch = Assert.Single(await worker.GetDueAsync(Token));
        var late = await worker.TryBeginAsync(dispatch, Token);
        Assert.NotNull(late);
        await worker.CompleteAsync(dispatch, late, rejected, Token);
        Assert.Null((await db.Reminders.AsNoTracking().SingleAsync(x => x.Id == late.Id)).NextAttemptAtUtc);
        clock.Now = Day.AddHours(18);
        Assert.Equal(new TimeOnly(18, 0), Assert.Single(await worker.GetDueAsync(Token)).Key.CriticalTime);
        Assert.Null(await worker.TryBeginAsync(dispatch, Token));
    }

    [ReminderProfilePostgresFact]
    public async Task BeforeSend_RevalidatesPaidPeriodAndChangesToPaymentAndProfile()
    {
        // A payment, changed term or disabled reminder must not use an already selected reservation.
        await using var db = new PersonalAssistantDbContext(ReminderProfileMigrationTests.CreateOptions());
        await db.Database.MigrateAsync();
        var (user, payment) = await SeedAsync(db, ReminderMode.Ordinary);
        var shifted = RecurringPayment.Create(user.Id, "Shifted", 50, "RUB", 1, RecurrenceUnit.Month, DueDate, Day);
        var disabled = RecurringPayment.Create(user.Id, "Disabled", 50, "RUB", 1, RecurrenceUnit.Month, DueDate, Day);
        var inactive = RecurringPayment.Create(user.Id, "Inactive", 50, "RUB", 1, RecurrenceUnit.Month, DueDate, Day);
        db.AddRange(shifted, disabled, inactive);
        await db.SaveChangesAsync();
        var repository = new ReminderRepository(db);
        var now = Day.AddHours(10);
        Assert.True(await repository.UpsertSnoozeAsync(user.Id, payment.Id, DueDate, now, now.AddHours(-1), Token));
        var worker = new ReminderService(repository, new ManualClock(now));
        var reservations = new List<(ReminderDispatch Dispatch, ReminderReservation Reservation)>();
        foreach (var dispatch in await worker.GetDueAsync(Token))
        {
            var reservation = await repository.TryReserveAsync(dispatch, now, now.AddMinutes(2), Token);
            Assert.NotNull(reservation);
            reservations.Add((dispatch, reservation));
        }
        Assert.Equal(4, reservations.Count);

        db.PaymentTransactions.Add(PaymentTransaction.Create(payment.Id, 100, 100, "RUB", DueDate, "2026-12-20", null, now));
        shifted.UpdateDetails(shifted.Name, shifted.Amount, shifted.Currency, 1, RecurrenceUnit.Month,
            DueDate.AddDays(5), PaymentMethod.Card, false, null, now);
        disabled.DisableReminders();
        inactive.Deactivate(now);
        await db.SaveChangesAsync();

        foreach (var (dispatch, reservation) in reservations)
            Assert.False(await repository.TryStartAttemptAsync(dispatch, reservation, now, now.AddMinutes(2), Token));
        Assert.All(await db.Reminders.AsNoTracking().ToListAsync(), reminder => Assert.Equal(0, reminder.AttemptCount));
    }

    [ReminderProfilePostgresFact]
    public async Task ChangingModeAndInterval_DoesNotReplayTheMorningOrReuseAnotherWallClockSlot()
    {
        // A previously delivered daily event covers the same morning, while a future critical time has its own key.
        await using var db = new PersonalAssistantDbContext(ReminderProfileMigrationTests.CreateOptions());
        await db.Database.MigrateAsync();
        var (user, _) = await SeedAsync(db, ReminderMode.Ordinary);
        var clock = new ManualClock(Day.AddHours(9));
        var worker = new ReminderService(new ReminderRepository(db), clock);
        var morning = Assert.Single(await worker.GetDueAsync(Token));
        var delivered = await worker.TryBeginAsync(morning, Token);
        Assert.NotNull(delivered);
        await worker.CompleteAsync(morning, delivered, new ReminderSendResult(ReminderSendOutcome.Sent, 91), Token);

        user.SetReminderProfile(ReminderProfile.DefaultFor(ReminderMode.Critical), clock.Now);
        await db.SaveChangesAsync();
        Assert.Null(await worker.TryBeginAsync(Assert.Single(await worker.GetDueAsync(Token)), Token));

        clock.Now = Day.AddHours(10);
        var firstCritical = Assert.Single(await worker.GetDueAsync(Token));
        var firstReservation = await worker.TryBeginAsync(firstCritical, Token);
        Assert.NotNull(firstReservation);
        await worker.CompleteAsync(firstCritical, firstReservation, new ReminderSendResult(ReminderSendOutcome.Sent, 92), Token);

        user.SetReminderProfile(new ReminderProfile(ReminderMode.Critical, 3, new TimeOnly(9, 0), OverdueMode.Daily,
            120, new TimeOnly(9, 0), new TimeOnly(21, 0)), clock.Now);
        await db.SaveChangesAsync();
        clock.Now = Day.AddHours(11);
        var next = Assert.Single(await worker.GetDueAsync(Token));
        Assert.Equal(new TimeOnly(11, 0), next.Key.CriticalTime);
        Assert.NotNull(await worker.TryBeginAsync(next, Token));

        var sent = await db.Reminders.AsNoTracking().Where(x => x.Status == ReminderDeliveryStatus.Sent).OrderBy(x => x.SentAtUtc).ToListAsync();
        Assert.Equal(new int?[] { 91, 92 }, sent.Select(x => x.TelegramMessageId));
        Assert.Equal(Day.AddHours(9), sent[0].SentAtUtc);
    }

    private static async Task<(User User, RecurringPayment Payment)> SeedAsync(PersonalAssistantDbContext db, ReminderMode mode)
    {
        var user = new User(50, 50, "Test", null, Day);
        user.SetReminderProfile(ReminderProfile.DefaultFor(mode), Day);
        var payment = RecurringPayment.Create(user.Id, "VPS", 100, "RUB", 1, RecurrenceUnit.Month, DueDate, Day);
        db.AddRange(user, payment);
        await db.SaveChangesAsync();
        return (user, payment);
    }

    private sealed class ManualClock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(Now);
    }
}
