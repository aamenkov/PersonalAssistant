using System.Globalization;
using System.Text.Json;
using PersonalAssistant.Domain;

namespace PersonalAssistant.Application;

public sealed class TimeZoneConversationService(
    IUserRepository users,
    IConversationStateRepository states)
{
    public async Task<string> BeginAsync(long telegramUserId, CancellationToken cancellationToken)
    {
        var user = await users.FindByTelegramUserIdAsync(telegramUserId, cancellationToken)
            ?? throw new InvalidOperationException("User is not registered.");
        var userId = user.Id;
        var existing = await states.FindAsync(userId, cancellationToken);
        var payload = JsonSerializer.Serialize(new TimeZoneDraft());
        if (existing is not null)
            existing.Reset(ConversationKind.TimeZone, payload, DateTime.UtcNow);
        else
            await states.AddAsync(ConversationState.Create(userId, ConversationKind.TimeZone, payload, DateTime.UtcNow), cancellationToken);
        await states.SaveChangesAsync(cancellationToken);
        return "Введите текущее местное время в формате ЧЧ:ММ, например 14:30:";
    }

    public async Task<string?> HandleInputAsync(long telegramUserId, string input, DateTime utcNow, CancellationToken cancellationToken)
    {
        var user = await users.FindByTelegramUserIdAsync(telegramUserId, cancellationToken);
        if (user is null)
            return null;
        var userId = user.Id;
        var state = await states.FindAsync(userId, cancellationToken);
        if (state is null || state.Kind != ConversationKind.TimeZone)
            return null;

        if (input.Trim().Equals("отмена", StringComparison.OrdinalIgnoreCase))
        {
            states.Remove(state);
            await states.SaveChangesAsync(cancellationToken);
            return "Изменение часового пояса отменено.";
        }

        var draft = JsonSerializer.Deserialize<TimeZoneDraft>(state.PayloadJson) ?? new TimeZoneDraft();
        if (draft.PendingTimeZoneId is not null)
        {
            if (!input.Trim().Equals("да", StringComparison.OrdinalIgnoreCase))
            {
                if (!input.Trim().Equals("нет", StringComparison.OrdinalIgnoreCase))
                    return "Выберите «Да» или «Нет».";

                draft.PendingTimeZoneId = null;
                state.Reset(ConversationKind.TimeZone, JsonSerializer.Serialize(draft), DateTime.UtcNow);
                await states.SaveChangesAsync(cancellationToken);
                return "Хорошо. Введите текущее местное время в формате ЧЧ:ММ:";
            }

            user.SetTimeZone(draft.PendingTimeZoneId, DateTime.UtcNow);
            states.Remove(state);
            await states.SaveChangesAsync(cancellationToken);
            return $"Часовой пояс сохранен: {FormatOffset(draft.PendingTimeZoneId)}.";
        }

        if (!TimeOnly.TryParseExact(input.Trim(), new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var localTime))
            return "Не удалось определить время. Введите его в формате ЧЧ:ММ, например 14:30:";

        var utcMinute = utcNow.AddSeconds(-utcNow.Second);
        var difference = localTime.ToTimeSpan() - utcMinute.TimeOfDay;
        if (difference > TimeSpan.FromHours(12))
            difference -= TimeSpan.FromDays(1);
        if (difference < TimeSpan.FromHours(-12))
            difference += TimeSpan.FromDays(1);

        var offsetHours = (int)Math.Round(difference.TotalHours, MidpointRounding.AwayFromZero);
        if (offsetHours is < 2 or > 12 || Math.Abs(difference.TotalHours - offsetHours) > 0.05)
            return "Для России укажите текущее местное время еще раз. Например: 14:30.";

        var timeZoneId = $"UTC{(offsetHours >= 0 ? "+" : "-")}{Math.Abs(offsetHours):00}:00";
        draft.PendingTimeZoneId = timeZoneId;
        state.Reset(ConversationKind.TimeZone, JsonSerializer.Serialize(draft), DateTime.UtcNow);
        await states.SaveChangesAsync(cancellationToken);
        return $"Определено смещение {FormatOffset(timeZoneId)}.\n\nСохранить часовой пояс?";
    }

    private static string FormatOffset(string timeZoneId) => timeZoneId.Replace("UTC", "UTC ", StringComparison.OrdinalIgnoreCase);

    private sealed class TimeZoneDraft
    {
        public string? PendingTimeZoneId { get; set; }
    }
}
