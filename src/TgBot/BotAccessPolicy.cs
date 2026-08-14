namespace PersonalAssistant.Bot;

internal sealed class BotAccessPolicy
{
    private readonly IReadOnlySet<long> allowedUserIds;

    private BotAccessPolicy(IReadOnlySet<long> allowedUserIds) => this.allowedUserIds = allowedUserIds;

    public static BotAccessPolicy Parse(string? value)
    {
        var values = (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var invalid = values.Where(x => !long.TryParse(x, out _)).ToArray();
        if (invalid.Length > 0)
            throw new InvalidOperationException($"Telegram:AllowedUserIds contains invalid values: {string.Join(", ", invalid)}");

        return new BotAccessPolicy(values.Select(long.Parse).ToHashSet());
    }

    public bool IsAllowed(long telegramUserId) => allowedUserIds.Count == 0 || allowedUserIds.Contains(telegramUserId);
}
