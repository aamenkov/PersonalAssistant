using System.Globalization;
using PersonalAssistant.Application;
using PersonalAssistant.Domain;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;
using User = PersonalAssistant.Domain.User;
using static PersonalAssistant.Bot.ReminderTelegramUi;

namespace PersonalAssistant.Bot;

internal sealed partial class ReminderTelegramHandler
{
    private async Task OpenSnoozeAsync(ITelegramBotClient client, User user, Screen screen, Guid paymentId, DateOnly dueDate, CancellationToken ct)
    {
        var target = await snoozes.GetTargetAsync(user, paymentId, dueDate, ct);
        if (target is null)
        {
            await ShowAsync(client, screen, "Этот срок уже не актуален или напоминания выключены.", PaymentBack(paymentId), ct);
            return;
        }

        var pending = target.Snooze is { ConsumedAtUtc: null } active ? active : null;
        var draft = new Draft(NewNonce(), screen, "snooze", PaymentId: paymentId, DueDate: dueDate, SnoozeId: pending?.Id);
        var entry = await OpenDraftAsync(user.Id, ConversationKind.ReminderSnooze, draft, ct);
        await ShowSnoozeMenuAsync(client, user, target, entry, ct);
    }

    private Task ShowSnoozeMenuAsync(ITelegramBotClient client, User user, ReminderCandidate target, DraftEntry entry, CancellationToken ct, string? error = null)
    {
        var draft = entry.Draft;
        var pending = target.Snooze is { ConsumedAtUtc: null } active ? active : null;
        if (pending?.Id != draft.SnoozeId)
            return FinishSnoozeAsync(client, entry, "Отсрочка уже изменилась. Откройте её заново.", ct);

        var zone = TimeZoneResolver.Resolve(user.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(Now, zone);
        var text = $"🔕 Отложить напоминания\n\n{target.Name}\nСрок платежа: {target.DueDate:dd.MM.yyyy}";
        if (pending is not null)
            text += $"\nОтложено до {TimeZoneInfo.ConvertTimeFromUtc(pending.UntilUtc, zone):dd.MM.yyyy HH:mm}.";
        if (error is not null)
            text += $"\n\n{error}";
        var buttons = new List<InlineKeyboardButton[]>
        {
            new[] { DraftButton(draft, "Через 1 час", "hour"), DraftButton(draft, "Через 3 часа", "three") }
        };
        if (localNow.TimeOfDay < TimeSpan.FromHours(19))
            buttons.Add([DraftButton(draft, "Сегодня до 19:00", "evening")]);
        buttons.Add([DraftButton(draft, "Завтра в 09:00", "tomorrow"), DraftButton(draft, "Указать время", "custom")]);
        if (draft.SnoozeId.HasValue)
            buttons.Add([DraftButton(draft, "Отменить отсрочку", "remove")]);
        buttons.Add([DraftButton(draft, "◀️ К платежу", "exit")]);
        return RenderDraftAsync(client, entry, text, new(buttons), ct);
    }

    private async Task HandleSnoozeActionAsync(ITelegramBotClient client, User user, DraftEntry entry, string action, CancellationToken ct)
    {
        var draft = entry.Draft;
        var paymentId = draft.PaymentId!.Value;
        var dueDate = draft.DueDate!.Value;
        if (action == "exit")
        {
            await ClearDraftAsync(entry, ct);
            await ShowAsync(client, draft.Screen, "Настройки отсрочки не изменены.", PaymentBack(paymentId), ct);
            return;
        }
        if (draft.Step == "input-snooze" && action == "menu")
        {
            if (await snoozes.GetTargetAsync(user, paymentId, dueDate, ct) is { } target)
                await ShowSnoozeMenuAsync(client, user, target, entry with { Draft = draft with { Step = "snooze" } }, ct);
            else
                await FinishSnoozeAsync(client, entry, "Этот срок уже не актуален.", ct);
            return;
        }
        if (draft.Step != "snooze")
            return;

        if (action == "remove" && draft.SnoozeId is { } snoozeId)
        {
            var cancelled = await snoozes.CancelAsync(user, paymentId, dueDate, snoozeId, ct);
            await FinishSnoozeAsync(client, entry, cancelled ? "Отсрочка отменена. Дальше действует обычное расписание."
                : "Отсрочка уже изменилась или этот срок не актуален.", ct);
            return;
        }
        if (action == "custom")
        {
            await ShowCustomSnoozeAsync(client, entry with { Draft = draft with { Step = "input-snooze" } }, user.TimeZoneId, null, ct);
            return;
        }
        var option = action switch
        {
            "hour" => ReminderSnoozeOption.InOneHour,
            "three" => ReminderSnoozeOption.InThreeHours,
            "evening" => ReminderSnoozeOption.TodayAt19,
            "tomorrow" => ReminderSnoozeOption.TomorrowAt9,
            _ => (ReminderSnoozeOption?)null
        };
        if (!option.HasValue)
            return;

        var now = Now;
        DateTime? until;
        try { until = ReminderSnoozeCalculator.CalculateUntil(option.Value, now, user.TimeZoneId, user.ReminderTimeLocal); }
        catch (ArgumentException) { until = null; }
        if (until is null || until <= now)
        {
            if (await snoozes.GetTargetAsync(user, paymentId, dueDate, ct) is { } target)
                await ShowSnoozeMenuAsync(client, user, target, entry, ct, "Это время уже прошло или недоступно. Выберите другое.");
            else
                await FinishSnoozeAsync(client, entry, "Этот срок уже не актуален.", ct);
            return;
        }
        await SaveSnoozeAsync(client, user, entry, until.Value, now, ct);
    }

    private Task ShowCustomSnoozeAsync(ITelegramBotClient client, DraftEntry entry, string zoneId, string? error, CancellationToken ct)
    {
        var draft = entry.Draft;
        var example = TimeZoneInfo.ConvertTimeFromUtc(Now, TimeZoneResolver.Resolve(zoneId)).Date.AddDays(1).AddHours(19).AddMinutes(30);
        var text = $"Введите дату и время в своём часовом поясе ({TelegramPresentation.TimeZone(zoneId, clock.GetUtcNow())}).\nНапример: {example:dd.MM.yyyy HH:mm}.";
        return RenderDraftAsync(client, entry, error is null ? text : $"{error}\n\n{text}", new([
            [DraftButton(draft, "◀️ Назад", "menu"), DraftButton(draft, "Отмена", "exit")]
        ]), ct);
    }

    private async Task HandleCustomSnoozeAsync(ITelegramBotClient client, User user, DraftEntry entry, string text, CancellationToken ct)
    {
        var now = Now;
        if (!DateTime.TryParseExact(text, "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            await ShowCustomSnoozeAsync(client, entry, user.TimeZoneId, "Не получилось распознать дату и время.", ct);
            return;
        }
        // DateTime conversion near its limits may be clamped by TimeZoneInfo; use only representable local dates.
        if (local.Year >= 9999 || ReminderSnoozeCalculator.ResolveLocal(DateOnly.FromDateTime(local), TimeOnly.FromDateTime(local), user.TimeZoneId)
            is not { } until || until <= now)
        {
            await ShowCustomSnoozeAsync(client, entry, user.TimeZoneId, "Выберите существующее время в будущем.", ct);
            return;
        }
        await SaveSnoozeAsync(client, user, entry, until, now, ct);
    }

    private async Task SaveSnoozeAsync(ITelegramBotClient client, User user, DraftEntry entry, DateTime until, DateTime now, CancellationToken ct)
    {
        var draft = entry.Draft;
        var success = await snoozes.SnoozeUntilAsync(user, draft.PaymentId!.Value, draft.DueDate!.Value, draft.SnoozeId, until, now, ct);
        var local = TimeZoneInfo.ConvertTimeFromUtc(until, TimeZoneResolver.Resolve(user.TimeZoneId));
        await FinishSnoozeAsync(client, entry, success.Succeeded ? $"🔕 Напоминания отложены до {local:dd.MM.yyyy HH:mm}."
            : "Отсрочка уже изменилась или этот срок не актуален.", ct);
    }

    private async Task FinishSnoozeAsync(ITelegramBotClient client, DraftEntry entry, string text, CancellationToken ct)
    {
        await ClearDraftAsync(entry, ct);
        var draft = entry.Draft;
        await ShowAsync(client, draft.Screen, text, new([
            [Button("Открыть отсрочку", TelegramCallbackData.Period("s", draft.PaymentId!.Value, draft.DueDate!.Value))],
            [Button("◀️ К платежу", TelegramCallbackData.Payment("back", draft.PaymentId.Value))]
        ]), ct);
    }

    private static InlineKeyboardMarkup PaymentBack(Guid paymentId) =>
        new([[Button("Открыть актуальный платёж", TelegramCallbackData.Payment("back", paymentId))]]);
}
