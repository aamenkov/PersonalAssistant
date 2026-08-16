namespace PersonalAssistant.Bot;

internal sealed class BotAdminPolicy
{
    private readonly long? adminUserId;

    private BotAdminPolicy(long? adminUserId) => this.adminUserId = adminUserId;

    public static BotAdminPolicy Parse(string? configuredAdminId, BotAccessPolicy accessPolicy)
    {
        if (!string.IsNullOrWhiteSpace(configuredAdminId))
        {
            if (!long.TryParse(configuredAdminId, out var parsed))
                throw new InvalidOperationException("Telegram:AdminUserId contains an invalid value.");
            return new BotAdminPolicy(parsed);
        }

        return new BotAdminPolicy(accessPolicy.SingleAllowedUserId);
    }

    public bool IsAdmin(long telegramUserId) => adminUserId == telegramUserId;
}
