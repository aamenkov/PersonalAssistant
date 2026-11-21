using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PersonalAssistant.Application;
using PersonalAssistant.Domain;
using PersonalAssistant.Infrastructure;

namespace PersonalAssistant.IntegrationTests;

public sealed class ReminderProfileMigrationTests
{
    internal const string TestPortVariable = "PA_029_TEST_POSTGRES_PORT";

    [ReminderProfilePostgresFact]
    public async Task Migration_PreservesExistingDataAndSupportsOwnerScopedProfileOverrides()
    {
        // Upgrade a database with real legacy rows, then verify profile round-trips and isolation.
        await using var db = new PersonalAssistantDbContext(CreateOptions());

        await db.GetService<IMigrator>().MigrateAsync("20260816092953_AddReminderSnooze");
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var otherPaymentId = Guid.NewGuid();
        var dueDate = new DateOnly(2026, 12, 20);
        var now = new DateTime(2026, 12, 17, 4, 0, 0, DateTimeKind.Utc);
        var snoozedUntil = now.AddHours(2);
        var legacyTime = new TimeOnly(7, 30);
        var otherLegacyTime = new TimeOnly(18, 45);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Users" ("Id", "TelegramUserId", "TelegramChatId", "DefaultCurrency", "TimeZoneId",
                "IsTimeZoneConfigured", "ReminderTimeLocal", "ReminderDaysBefore", "CreatedAtUtc", "UpdatedAtUtc")
            VALUES ({userId}, 100, 100, 'RUB', 'UTC+03:00', TRUE, {legacyTime}, 0, {now}, {now}),
                   ({otherUserId}, 200, 200, 'RUB', 'Europe/Berlin', TRUE, {otherLegacyTime}, 7, {now}, {now})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "RecurringPayments" ("Id", "UserId", "Name", "Amount", "Currency", "RecurrenceInterval", "RecurrenceUnit",
                "NextPaymentDate", "ScheduleDayOfMonth", "IsLastDayOfMonth", "PaymentMethod", "IsAutoDebit", "IsActive", "CreatedAtUtc", "UpdatedAtUtc")
            VALUES ({paymentId}, {userId}, 'Internet', 100, 'RUB', 1, 2, {dueDate}, 20, FALSE, 0, FALSE, TRUE, {now}, {now}),
                   ({otherPaymentId}, {userId}, 'Phone', 100, 'RUB', 1, 2, {dueDate}, 20, FALSE, 0, FALSE, TRUE, {now}, {now})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Reminders" ("Id", "RecurringPaymentId", "DueDate", "LocalDate", "Kind", "SentAtUtc")
            VALUES ({Guid.NewGuid()}, {paymentId}, {dueDate}, {dueDate.AddDays(-3)}, 0, {now}),
                   ({Guid.NewGuid()}, {otherPaymentId}, {dueDate}, {dueDate}, 1, {now.AddDays(3).AddHours(1)})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ReminderSnoozes" ("Id", "RecurringPaymentId", "DueDate", "SnoozedUntilUtc", "CreatedAtUtc")
            VALUES ({Guid.NewGuid()}, {paymentId}, {dueDate}, {snoozedUntil}, {now})
            """);

        const string profilesMigration = "20261121195830_AddReminderProfiles";
        await db.GetService<IMigrator>().MigrateAsync(profilesMigration);
        var legacyTimestampQuery = db.Database.SqlQuery<DateTime>($"""
            SELECT "SentAtUtc" AS "Value" FROM "Reminders" WHERE "RecurringPaymentId" = {paymentId}
            """);
        Assert.Equal(now, await legacyTimestampQuery.SingleAsync());
        await db.Database.MigrateAsync();
        await db.GetService<IMigrator>().MigrateAsync(profilesMigration);
        Assert.Equal(now, await legacyTimestampQuery.SingleAsync());
        await db.Database.MigrateAsync();

        var token = CancellationToken.None;
        var legacy = new ReminderProfile(ReminderMode.Ordinary, 0, legacyTime, OverdueMode.Daily);
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId);
        var otherUser = await db.Users.AsNoTracking().SingleAsync(x => x.Id == otherUserId);
        Assert.Equal(legacy, user.GetReminderProfile());
        Assert.Equal("UTC+03:00", user.TimeZoneId);
        Assert.Equal(new ReminderProfile(ReminderMode.Ordinary, 7, otherLegacyTime, OverdueMode.Daily), otherUser.GetReminderProfile());
        Assert.Equal("Europe/Berlin", otherUser.TimeZoneId);
        var historical = await db.Reminders.AsNoTracking().SingleAsync(x => x.RecurringPaymentId == paymentId);
        Assert.Equal(now, historical.ClaimedAtUtc);
        Assert.Null(historical.SentAtUtc);
        Assert.Equal(ReminderDeliveryStatus.HistoricalUnknown, historical.Status);
        Assert.Equal("daily", historical.SlotKey);
        var snooze = await db.ReminderSnoozes.SingleAsync();
        Assert.Equal(snoozedUntil, snooze.SnoozedUntilUtc);
        Assert.Null(snooze.ConsumedAtUtc);

        var reminderRepository = new ReminderRepository(db);
        var oldCandidate = Assert.Single(await reminderRepository.GetCandidatesAsync(token), x => x.PaymentId == otherPaymentId);
        var dueNow = new DateTime(2026, 12, 20, 10, 0, 0, DateTimeKind.Utc);
        var oldDispatch = ReminderService.SelectDue(oldCandidate, dueNow);
        Assert.NotNull(oldDispatch);
        Assert.Null(await reminderRepository.TryReserveAsync(oldDispatch, dueNow, dueNow.AddMinutes(2), token));

        var snoozedCandidate = Assert.Single(await reminderRepository.GetCandidatesAsync(token), x => x.PaymentId == paymentId);
        var migratedSnooze = ReminderService.SelectDue(snoozedCandidate, snoozedUntil);
        Assert.NotNull(migratedSnooze);
        Assert.Equal(snooze.Id, migratedSnooze.SnoozeId);

        var users = new UserSettingsService(new UserRepository(db));
        var profiles = new PaymentReminderSettingsService(new PaymentRepository(db));
        Assert.Equal(new EffectiveReminderProfile(legacy, true), await profiles.GetEffectiveAsync(userId, paymentId, token));
        Assert.Null(await profiles.GetEffectiveAsync(otherUserId, paymentId, token));
        Assert.False(await profiles.DisableAsync(otherUserId, paymentId, token));

        var overrideProfile = ReminderProfile.DefaultFor(ReminderMode.Critical);
        Assert.True(await profiles.SetProfileAsync(userId, paymentId, overrideProfile, token));
        db.ChangeTracker.Clear();
        var newDefault = new ReminderProfile(ReminderMode.Persistent, 5, new TimeOnly(12, 0), OverdueMode.Disabled);
        await users.SetReminderProfileAsync(100, newDefault, token);
        Assert.False(await users.TrySetReminderDaysAsync(100, 0, token));
        Assert.False(await users.TrySetReminderTimeAsync(100, new TimeOnly(18, 0), token));
        Assert.Equal(new EffectiveReminderProfile(overrideProfile, false), await profiles.GetEffectiveAsync(userId, paymentId, token));
        Assert.Equal(new EffectiveReminderProfile(newDefault, true), await profiles.GetEffectiveAsync(userId, otherPaymentId, token));

        Assert.True(await profiles.DisableAsync(userId, paymentId, token));
        db.ChangeTracker.Clear();
        Assert.Equal(new EffectiveReminderProfile(null, false), await profiles.GetEffectiveAsync(userId, paymentId, token));
        Assert.True(await profiles.SetProfileAsync(userId, paymentId, overrideProfile, token));
        db.ChangeTracker.Clear();
        Assert.Equal(new EffectiveReminderProfile(overrideProfile, false), await profiles.GetEffectiveAsync(userId, paymentId, token));

        Assert.True(await profiles.ResetAsync(userId, paymentId, token));
        db.ChangeTracker.Clear();
        Assert.Equal(new EffectiveReminderProfile(newDefault, true), await profiles.GetEffectiveAsync(userId, paymentId, token));
    }

    internal static DbContextOptions<PersonalAssistantDbContext> CreateOptions()
    {
        Assert.True(ushort.TryParse(Environment.GetEnvironmentVariable(TestPortVariable), out var port) && port > 0);
        return new DbContextOptionsBuilder<PersonalAssistantDbContext>()
            .UseNpgsql($"Host=127.0.0.1;Port={port};Database=pa_029_validation_{Guid.NewGuid():N};Username=pa_029_test;Password=pa_029_test_only")
            .Options;
    }
}

internal sealed class ReminderProfilePostgresFactAttribute : FactAttribute
{
    public ReminderProfilePostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ReminderProfileMigrationTests.TestPortVariable)))
            Skip = "Requires the dedicated local PostgreSQL container for task 029.";
    }
}
