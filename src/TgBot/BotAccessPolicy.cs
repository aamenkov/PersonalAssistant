namespace PersonalAssistant.Bot;

internal sealed class BotAccessPolicy
{
    private readonly IReadOnlySet<long> allowedUserIds;

    private BotAccessPolicy(IReadOnlySet<long> allowedUserIds) => this.allowedUserIds = allowedUserIds;

    public static BotAccessPolicy Parse(string? value, long? additionalAllowedUserId = null)
    {
        var values = (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var invalid = values.Where(x => !long.TryParse(x, out _)).ToArray();
        if (invalid.Length > 0)
            throw new InvalidOperationException($"Telegram:AllowedUserIds contains invalid values: {string.Join(", ", invalid)}");

        var allowed = values.Select(long.Parse).ToHashSet();
        if (values.Length > 0 && additionalAllowedUserId.HasValue)
            allowed.Add(additionalAllowedUserId.Value);
        return new BotAccessPolicy(allowed);
    }

    public bool IsAllowed(long telegramUserId) => allowedUserIds.Count == 0 || allowedUserIds.Contains(telegramUserId);

    public long? SingleAllowedUserId => allowedUserIds.Count == 1 ? allowedUserIds.Single() : null;
}
