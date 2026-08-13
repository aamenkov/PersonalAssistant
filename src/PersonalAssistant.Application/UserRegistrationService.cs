using PersonalAssistant.Domain;

namespace PersonalAssistant.Application;

public interface IUserRepository
{
    Task<User?> FindByTelegramUserIdAsync(long telegramUserId, CancellationToken cancellationToken);
    Task AddAsync(User user, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed class UserTimeZoneService(IUserRepository users)
{
    public async Task SetAsync(long telegramUserId, string timeZoneId, CancellationToken cancellationToken)
    {
        TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var user = await users.FindByTelegramUserIdAsync(telegramUserId, cancellationToken)
            ?? throw new InvalidOperationException("User is not registered.");

        user.SetTimeZone(timeZoneId, DateTime.UtcNow);
        await users.SaveChangesAsync(cancellationToken);
    }
}

public sealed class UserSettingsService(IUserRepository users)
{
    public Task<User?> FindAsync(long telegramUserId, CancellationToken cancellationToken) =>
        users.FindByTelegramUserIdAsync(telegramUserId, cancellationToken);

    public async Task SetDefaultCurrencyAsync(long telegramUserId, string currency, CancellationToken cancellationToken)
    {
        var user = await users.FindByTelegramUserIdAsync(telegramUserId, cancellationToken)
            ?? throw new InvalidOperationException("User is not registered.");
        user.SetDefaultCurrency(currency, DateTime.UtcNow);
        await users.SaveChangesAsync(cancellationToken);
    }

    public async Task SetReminderSettingsAsync(long telegramUserId, TimeOnly reminderTimeLocal, int reminderDaysBefore, CancellationToken cancellationToken)
    {
        var user = await users.FindByTelegramUserIdAsync(telegramUserId, cancellationToken)
            ?? throw new InvalidOperationException("User is not registered.");
        user.SetReminderSettings(reminderTimeLocal, reminderDaysBefore, DateTime.UtcNow);
        await users.SaveChangesAsync(cancellationToken);
    }

    public async Task SetReminderTimeAsync(long telegramUserId, TimeOnly reminderTimeLocal, CancellationToken cancellationToken)
    {
        var user = await users.FindByTelegramUserIdAsync(telegramUserId, cancellationToken)
            ?? throw new InvalidOperationException("User is not registered.");
        user.SetReminderSettings(reminderTimeLocal, user.ReminderDaysBefore, DateTime.UtcNow);
        await users.SaveChangesAsync(cancellationToken);
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
