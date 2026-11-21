using PersonalAssistant.Domain;

namespace PersonalAssistant.Application;

public interface IUserRepository
{
    Task<User?> FindByTelegramUserIdAsync(long telegramUserId, CancellationToken cancellationToken);
    Task AddAsync(User user, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
    Task<bool> TrySetReminderProfileAsync(long telegramUserId, ReminderProfile original, ReminderProfile profile,
        DateTime utcNow, CancellationToken cancellationToken);
}

public sealed class UserTimeZoneService(IUserRepository users)
{
    public async Task SetAsync(long telegramUserId, string timeZoneId, CancellationToken cancellationToken)
    {
        TimeZoneResolver.Resolve(timeZoneId);
        var user = await users.FindByTelegramUserIdAsync(telegramUserId, cancellationToken)
            ?? throw new InvalidOperationException("User is not registered.");

        user.SetTimeZone(timeZoneId, DateTime.UtcNow);
        await users.SaveChangesAsync(cancellationToken);
    }
}

public sealed class UserSettingsService(IUserRepository users)
{
    public Task<bool> TrySetReminderProfileAsync(long telegramUserId, ReminderProfile original, ReminderProfile profile,
        DateTime utcNow, CancellationToken cancellationToken) =>
        users.TrySetReminderProfileAsync(telegramUserId, original, profile, utcNow, cancellationToken);

    public Task<User?> FindAsync(long telegramUserId, CancellationToken cancellationToken) =>
        users.FindByTelegramUserIdAsync(telegramUserId, cancellationToken);

    public async Task SetDefaultCurrencyAsync(long telegramUserId, string currency, CancellationToken cancellationToken)
    {
        var user = await users.FindByTelegramUserIdAsync(telegramUserId, cancellationToken)
            ?? throw new InvalidOperationException("User is not registered.");
        user.SetDefaultCurrency(currency, DateTime.UtcNow);
        await users.SaveChangesAsync(cancellationToken);
    }

    public async Task SetReminderProfileAsync(long telegramUserId, ReminderProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var user = await users.FindByTelegramUserIdAsync(telegramUserId, cancellationToken)
            ?? throw new InvalidOperationException("User is not registered.");
        user.SetReminderProfile(profile, DateTime.UtcNow);
        await users.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> TrySetReminderDaysAsync(long telegramUserId, int daysBefore, CancellationToken cancellationToken) =>
        TryUpdateOrdinaryReminderAsync(telegramUserId, daysBefore, null, cancellationToken);

    public Task<bool> TrySetReminderTimeAsync(long telegramUserId, TimeOnly reminderTimeLocal, CancellationToken cancellationToken) =>
        TryUpdateOrdinaryReminderAsync(telegramUserId, null, reminderTimeLocal, cancellationToken);

    private async Task<bool> TryUpdateOrdinaryReminderAsync(
        long telegramUserId, int? daysBefore, TimeOnly? reminderTimeLocal, CancellationToken cancellationToken)
    {
        var user = await users.FindByTelegramUserIdAsync(telegramUserId, cancellationToken)
            ?? throw new InvalidOperationException("User is not registered.");
        if (user.ReminderMode != ReminderMode.Ordinary)
            return false;

        user.SetReminderSettings(reminderTimeLocal ?? user.ReminderTimeLocal, daysBefore ?? user.ReminderDaysBefore, DateTime.UtcNow);
        await users.SaveChangesAsync(cancellationToken);
        return true;
    }
}

public sealed class UserRegistrationService(IUserRepository users)
{
    public async Task<User> RegisterOrUpdateAsync(
        long telegramUserId,
        long telegramChatId,
        string? firstName,
        string? username,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var user = await users.FindByTelegramUserIdAsync(telegramUserId, cancellationToken);
        if (user is null)
        {
            user = new User(telegramUserId, telegramChatId, firstName, username, now);
            await users.AddAsync(user, cancellationToken);
        }
        else
        {
            user.UpdateTelegramProfile(telegramChatId, firstName, username, now);
        }

        await users.SaveChangesAsync(cancellationToken);
        return user;
    }
}
