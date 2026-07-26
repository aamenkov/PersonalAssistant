using TelegramAssistant.Domain;

namespace TelegramAssistant.Application;

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
