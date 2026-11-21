using System.Globalization;
using System.Text.Json;
using PersonalAssistant.Application;
using PersonalAssistant.Domain;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using User = PersonalAssistant.Domain.User;
using static PersonalAssistant.Bot.ReminderTelegramUi;

namespace PersonalAssistant.Bot;

internal sealed partial class ReminderTelegramHandler(
    IUserRepository users,
    UserSettingsService userSettings,
    PaymentReminderSettingsService paymentSettings,
    PaymentService payments,
    IConversationStateRepository states,
    ReminderSnoozeService snoozes,
    TimeProvider clock)
{
    private const string Stale = "Этот экран уже не актуален. Откройте настройки или платёж заново.";
    private const string ProfileConflict = "Настройки успели измениться. Откройте редактор заново.";
    private static readonly TimeSpan DraftLifetime = TimeSpan.FromMinutes(30);
    private static readonly string[] CriticalFields = ["interval", "start", "end"];
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    internal async Task HandleCallbackAsync(ITelegramBotClient client, CallbackQuery callback, string data, CancellationToken ct)
    {
        var user = await users.FindByTelegramUserIdAsync(callback.From.Id, ct);
        if (user is null || callback.Message is not { Chat.Type: ChatType.Private } message || message.Chat.Id != user.TelegramChatId)
        {
            await client.AnswerCallbackQuery(callback.Id, "Откройте личный чат с ботом и выполните /start.", showAlert: true, cancellationToken: ct);
            return;
        }

        await client.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
        var screen = new Screen(message.Chat.Id, message.Id);
        if (TelegramCallbackData.TryReadPeriod(data, out var action, out var paymentId, out var dueDate) && action == "s")
        {
            await OpenSnoozeAsync(client, user, screen, paymentId, dueDate, ct);
            return;
        }

        if (data.StartsWith(TelegramCallbackData.ReminderSettingsPrefix, StringComparison.Ordinal))
        {
            await HandleSettingsAsync(client, user, screen, data[TelegramCallbackData.ReminderSettingsPrefix.Length..], ct);
            return;
        }

        var parts = data.Split(':', 3);
        if (parts is not ["nd", _, _] || await LoadDraftAsync(user.Id, screen.ChatId, ct) is not { } entry
            || entry.Draft.Nonce != parts[1] || entry.Draft.Screen != screen)
        {
            await client.SendMessage(screen.ChatId, Stale, cancellationToken: ct);
            return;
        }

        if (entry.State.Kind == ConversationKind.ReminderSnooze)
            await HandleSnoozeActionAsync(client, user, entry, parts[2], ct);
        else
            await HandleProfileActionAsync(client, user, entry, parts[2], ct);
    }

    internal async Task<bool> HandleTextAsync(ITelegramBotClient client, User user, Message message, string text, CancellationToken ct)
    {
        if (message.Chat.Type != ChatType.Private || message.Chat.Id != user.TelegramChatId)
            return false;
        if (await LoadDraftAsync(user.Id, message.Chat.Id, ct) is not { } entry)
            return false;
        if (text.Equals("отмена", StringComparison.OrdinalIgnoreCase))
        {
            if (entry.State.Kind == ConversationKind.ReminderSnooze)
                await HandleSnoozeActionAsync(client, user, entry, "exit", ct);
            else
                await HandleProfileActionAsync(client, user, entry, "exit", ct);
            return true;
        }
        if (!entry.Draft.Step.StartsWith("input-", StringComparison.Ordinal))
        {
            await client.SendMessage(message.Chat.Id, "Выберите действие на экране настроек или нажмите «Отмена».", cancellationToken: ct);
            return true;
        }
        if (entry.State.Kind == ConversationKind.ReminderSnooze)
            await HandleCustomSnoozeAsync(client, user, entry, text.Trim(), ct);
        else
            await ChangeFieldAsync(client, user, entry, entry.Draft.Step[6..], text.Trim(), ct);
        return true;
    }

    private async Task HandleSettingsAsync(ITelegramBotClient client, User user, Screen screen, string data, CancellationToken ct)
    {
        var parts = data.Split(':', 2);
        var action = parts[0];
        Guid? id = parts.Length == 2 && Guid.TryParseExact(parts[1], "N", out var parsed) ? parsed : null;
        switch (action)
        {
            case "global":
            case "sample" when id.HasValue:
                await ShowProfileAsync(client, user, screen, null, id, ct);
                return;
            case "payment" when id.HasValue:
                await ShowProfileAsync(client, user, screen, id, null, ct);
                return;
            case "page" when parts.Length == 2 && TryPage(parts[1], out var page):
                await ShowExamplesAsync(client, user, screen, null, page, ct);
                return;
            case "edit-global":
            case "edit-payment" or "disable" or "inherit" or "enable" when id.HasValue:
                await StartProfileAsync(client, user, screen, action == "edit-global" ? null : id,
                    action == "edit-global" ? id : null, action, ct);
                return;
            case "edit-back" when id.HasValue:
                if (await payments.GetDetailsAsync(user.Id, id.Value, ct) is { } payment)
                {
                    await ShowAsync(client, screen, $"✏️ Что изменить в платеже «{payment.Name}»?", TelegramUi.EditFieldsKeyboard(payment.Id), ct);
                    return;
                }
                break;
        }
        await ShowAsync(client, screen, Stale, SettingsBack(), ct);
    }

    private Task<EffectiveReminderProfile?> ReadProfileAsync(User user, Guid? paymentId, CancellationToken ct) =>
        paymentId.HasValue ? paymentSettings.GetEffectiveAsync(user.Id, paymentId.Value, ct)
            : Task.FromResult<EffectiveReminderProfile?>(new(user.GetReminderProfile(), false));

    private async Task ShowProfileAsync(ITelegramBotClient client, User user, Screen screen, Guid? paymentId, Guid? sampleId, CancellationToken ct)
    {
        var effective = await ReadProfileAsync(user, paymentId, ct);
        var payment = paymentId is { } id ? await payments.GetDetailsAsync(user.Id, id, ct) : null;
        if (effective is null || (paymentId.HasValue && payment is null))
        {
            await ShowAsync(client, screen, "Платёж недоступен.", SettingsBack(), ct);
            return;
        }

        var buttons = new List<InlineKeyboardButton[]>();
        var heading = payment is not null ? $"🔔 Напоминания — {payment.Name}" : "🔔 Напоминания";
        var text = heading;
        if (payment is not null)
            text += effective.IsInherited ? "\n\nИспользуются общие настройки."
                : effective.Profile is null ? "\n\nУведомления выключены." : "\n\nИндивидуальные настройки.";

        if (effective.Profile is { } profile)
        {
            var (sample, multiple) = await GetExampleAsync(user.Id, payment, sampleId, ct);
            text += $"\n\n{FormatProfile(profile)}\n\n{FormatPreview(profile, user.TimeZoneId, clock.GetUtcNow(), sample?.Name, sample?.DueDate, payment is null)}";
            buttons.Add([Button(payment is not null && effective.IsInherited ? "Настроить отдельно" : "Изменить",
                payment is null ? TelegramCallbackData.ReminderSettings("edit-global", sample?.Id) : TelegramCallbackData.ReminderSettings("edit-payment", payment.Id))]);
            if (multiple)
                buttons.Add([Button("Другой пример", TelegramCallbackData.ReminderSettings("page:0"))]);
            if (payment is not null && !effective.IsInherited)
                buttons.Add([Button("Вернуть общие настройки", TelegramCallbackData.ReminderSettings("inherit", payment.Id))]);
            if (payment is not null)
            {
                buttons.Add([Button("Отсрочка", TelegramCallbackData.Period("s", payment.Id, payment.NextPaymentDate))]);
                buttons.Add([Button("Отключить уведомления", TelegramCallbackData.ReminderSettings("disable", payment.Id))]);
            }
        }
        else if (payment is not null)
            buttons.Add([Button("Включить с общими настройками", TelegramCallbackData.ReminderSettings("enable", payment.Id))]);

        buttons.Add([Button("◀️ Назад", payment is null ? TelegramCallbackData.Setting("back") : TelegramCallbackData.ReminderSettings("edit-back", payment.Id))]);
        await ShowAsync(client, screen, text, new(buttons), ct);
    }

    private async Task StartProfileAsync(ITelegramBotClient client, User user, Screen screen, Guid? paymentId, Guid? sampleId,
        string action, CancellationToken ct)
    {
        var original = await ReadProfileAsync(user, paymentId, ct);
        if (original is null || (action == "enable" ? original.Profile is not null : original.Profile is null)
            || (action == "inherit" && original.IsInherited))
        {
            await ShowProfileAsync(client, user, screen, paymentId, sampleId, ct);
            return;
        }
        if (action == "enable")
        {
            if (await paymentSettings.TryChangeAsync(user.Id, paymentId!.Value, original, new(null, true), ct))
                await ShowProfileAsync(client, user, screen, paymentId, sampleId, ct);
            else
                await ShowAsync(client, screen, ProfileConflict, ProfileBack(paymentId), ct);
            return;
        }
        var draft = new Draft(NewNonce(), screen, action is "disable" or "inherit" ? action : "editor",
            PaymentId: paymentId, SampleId: sampleId, Original: original, Profile: original.Profile);
        var entry = await OpenDraftAsync(user.Id, ConversationKind.ReminderProfile, draft, ct);
        if (action is "disable" or "inherit")
        {
            var question = action == "inherit" ? "Вернуть общие настройки? Индивидуальные настройки будут удалены."
                : original.IsInherited ? "Отключить уведомления для этого платежа?"
                : "Отключить уведомления? Индивидуальные настройки будут удалены. При включении будут действовать общие.";
            await RenderDraftAsync(client, entry, question, new([
                [DraftButton(draft, "Подтвердить", "confirm")], [DraftButton(draft, "Отмена", "exit")]
            ]), ct);
        }
        else
            await ShowEditorAsync(client, user, entry, ct);
    }

    private async Task HandleProfileActionAsync(ITelegramBotClient client, User user, DraftEntry entry, string action, CancellationToken ct)
    {
        var draft = entry.Draft;
        switch (action)
        {
            case "exit":
                await ClearDraftAsync(entry, ct);
                await ShowProfileAsync(client, user, draft.Screen, draft.PaymentId, draft.SampleId, ct);
                return;
            case "save" when draft.Step == "editor" && draft.Profile is { } profile:
                var saved = draft.PaymentId.HasValue
                    ? await paymentSettings.TryChangeAsync(user.Id, draft.PaymentId.Value, draft.Original!, new(profile, false), ct)
                    : await userSettings.TrySetReminderProfileAsync(user.TelegramUserId, draft.Original!.Profile!, profile, Now, ct);
                await FinishProfileAsync(client, user, entry, saved, ct);
                return;
            case "confirm" when draft.Step is "disable" or "inherit":
                var changed = await paymentSettings.TryChangeAsync(user.Id, draft.PaymentId!.Value, draft.Original!,
                    new(null, draft.Step == "inherit"), ct);
                await FinishProfileAsync(client, user, entry, changed, ct);
                return;
            case "editor" when draft.Step is not ("disable" or "inherit"):
                await ShowEditorAsync(client, user, entry with { Draft = draft with { Step = "editor" } }, ct);
                return;
        }
        if (draft.Profile is null || draft.Step is "disable" or "inherit")
            return;
        if (action.StartsWith("f:", StringComparison.Ordinal) && draft.Step == "editor")
        {
            var field = action[2..];
            if (field is "mode" or "days" or "time" or "overdue" || CriticalFields.Contains(field) && draft.Profile.Mode == ReminderMode.Critical)
                await ShowFieldAsync(client, entry with { Draft = draft with { Step = field } }, field, ct);
        }
        else if (action == $"custom:{draft.Step}" && draft.Step is "days" or "time" or "start" or "end")
            await ShowFieldAsync(client, entry with { Draft = draft with { Step = $"input-{draft.Step}" } }, draft.Step, ct);
        else if (action.StartsWith($"v:{draft.Step}:", StringComparison.Ordinal) && draft.Step is "mode" or "days" or "time" or "overdue" or "interval" or "start" or "end")
            await ChangeFieldAsync(client, user, entry, draft.Step, action[(draft.Step.Length + 3)..], ct);
        else if (draft.PaymentId is null && action.StartsWith("page:", StringComparison.Ordinal)
            && draft.Step is "editor" or "examples" && TryPage(action[5..], out var page))
            await ShowExamplesAsync(client, user, draft.Screen, entry with { Draft = draft with { Step = "examples" } }, page, ct);
        else if (draft.PaymentId is null && draft.Step == "examples" && action.StartsWith("sample:", StringComparison.Ordinal)
            && Guid.TryParseExact(action[7..], "N", out var id) && await payments.GetDetailsAsync(user.Id, id, ct) is not null)
            await ShowEditorAsync(client, user, entry with { Draft = draft with { Step = "editor", SampleId = id } }, ct);
    }

    private async Task FinishProfileAsync(ITelegramBotClient client, User user, DraftEntry entry, bool saved, CancellationToken ct)
    {
        await ClearDraftAsync(entry, ct);
        if (saved)
            await ShowProfileAsync(client, user, entry.Draft.Screen, entry.Draft.PaymentId, entry.Draft.SampleId, ct);
        else
            await ShowAsync(client, entry.Draft.Screen, ProfileConflict, ProfileBack(entry.Draft.PaymentId), ct);
    }

    private async Task ShowEditorAsync(ITelegramBotClient client, User user, DraftEntry entry, CancellationToken ct)
    {
        var draft = entry.Draft;
        var profile = draft.Profile!;
        var payment = draft.PaymentId is { } id ? await payments.GetDetailsAsync(user.Id, id, ct) : null;
        if (draft.PaymentId.HasValue && payment is null)
        {
            await ClearDraftAsync(entry, ct);
            await ShowAsync(client, draft.Screen, "Платёж недоступен.", SettingsBack(), ct);
            return;
        }
        var (sample, multiple) = await GetExampleAsync(user.Id, payment, draft.SampleId, ct);
        var buttons = new List<InlineKeyboardButton[]>
        {
            new[] { DraftButton(draft, "Изменить режим", "f:mode") },
            new[] { DraftButton(draft, "Дни до оплаты", "f:days"), DraftButton(draft, "Время", "f:time") }
        };
        if (profile.Mode == ReminderMode.Critical)
        {
            buttons.Add([DraftButton(draft, "Интервал", "f:interval")]);
            buttons.Add([DraftButton(draft, "Начало окна", "f:start"), DraftButton(draft, "Конец окна", "f:end")]);
        }
        buttons.Add([DraftButton(draft, "После просрочки", "f:overdue")]);
        if (multiple)
            buttons.Add([DraftButton(draft, "Другой пример", "page:0")]);
        buttons.Add([DraftButton(draft, "Сохранить", "save"), DraftButton(draft, "Отмена", "exit")]);
        var heading = payment is null ? "🔔 Общие напоминания" : $"🔔 Напоминания — {payment.Name}";
        var text = $"{heading}\n\nЧерновик — изменения пока не действуют.\n\n{FormatProfile(profile)}\n\n"
            + FormatPreview(profile, user.TimeZoneId, clock.GetUtcNow(), sample?.Name, sample?.DueDate, payment is null);
        await RenderDraftAsync(client, entry with { Draft = draft with { SampleId = payment is null ? sample?.Id : null } }, text, new(buttons), ct);
    }

    private async Task<(ReminderPreviewPayment? Sample, bool Multiple)> GetExampleAsync(Guid userId, PaymentDetails? payment, Guid? sampleId, CancellationToken ct)
    {
        if (payment is not null)
            return (new(payment.Id, payment.Name, payment.NextPaymentDate), false);
        var options = await paymentSettings.ListExamplesAsync(userId, 0, 2, ct);
        var sample = options.FirstOrDefault(x => x.Id == sampleId);
        if (sample is null && sampleId is { } id && await payments.GetDetailsAsync(userId, id, ct) is { } chosen)
            sample = new(chosen.Id, chosen.Name, chosen.NextPaymentDate);
        return (sample ?? options.FirstOrDefault(), options.Count > 1);
    }

    private async Task ShowExamplesAsync(ITelegramBotClient client, User user, Screen screen, DraftEntry? entry, int page, CancellationToken ct)
    {
        const int pageSize = 5;
        var options = await paymentSettings.ListExamplesAsync(user.Id, page * pageSize, pageSize + 1, ct);
        string Action(string kind, string value) => entry is null ? TelegramCallbackData.ReminderSettings($"{kind}:{value}")
            : TelegramCallbackData.ReminderDraft(entry.Draft.Nonce, $"{kind}:{value}");
        var buttons = options.Take(pageSize).Select(x => new[] { Button(ShortName(x.Name), Action("sample", x.Id.ToString("N"))) }).ToList();
        var navigation = new List<InlineKeyboardButton>();
        if (page > 0)
            navigation.Add(Button("◀", Action("page", (page - 1).ToString(CultureInfo.InvariantCulture))));
        if (options.Count > pageSize)
            navigation.Add(Button("▶", Action("page", (page + 1).ToString(CultureInfo.InvariantCulture))));
        if (navigation.Count > 0)
            buttons.Add(navigation.ToArray());
        buttons.Add([entry is null ? Button("◀️ Назад", TelegramCallbackData.ReminderSettings("global"))
            : DraftButton(entry.Draft, "◀️ Назад", "editor")]);
        var text = options.Count == 0 ? "Нет активных платежей на этой странице." : "📅 Для какого платежа показать пример?";
        if (entry is null)
            await ShowAsync(client, screen, text, new(buttons), ct);
        else
            await RenderDraftAsync(client, entry, text, new(buttons), ct);
    }

    private Task ShowFieldAsync(ITelegramBotClient client, DraftEntry entry, string field, CancellationToken ct, string? error = null)
    {
        var draft = entry.Draft;
        var profile = draft.Profile!;
        var rows = new List<InlineKeyboardButton[]>();
        void Add(params (string Label, string Value)[] choices) =>
            rows.Add(choices.Select(choice => DraftButton(draft, choice.Label, $"v:{field}:{choice.Value}")).ToArray());
        var prompt = field switch
        {
            "mode" => "Обычный — заранее и в день оплаты.\nНастойчивый — ежедневно до оплаты.\nКритический — с повторами в день оплаты.",
            "days" => profile.Mode == ReminderMode.Ordinary ? "За сколько дней напомнить? От 0 до 30; 0 — только в день оплаты." : "За сколько дней начать напоминать? От 1 до 30.",
            "overdue" => "Как напоминать после просрочки?",
            "interval" => "Как часто повторять в день оплаты?",
            "start" => "Во сколько начинать повторы?",
            "end" => "До какого времени повторять?",
            _ => "Во сколько напоминать?"
        };
        if (draft.Step.StartsWith("input-", StringComparison.Ordinal))
            prompt += field == "days" ? "\nВведите число, например 5." : "\nВведите время в формате ЧЧ:ММ, например 08:45.";
        else
        {
            switch (field)
            {
                case "mode":
                    foreach (var mode in Enum.GetValues<ReminderMode>())
                        Add((ModeName(mode), ((int)mode).ToString(CultureInfo.InvariantCulture)));
                    break;
                case "days":
                    Add(("1 день", "1"), ("3 дня", "3"), ("7 дней", "7"));
                    if (profile.Mode == ReminderMode.Ordinary)
                        Add(("В день оплаты", "0"));
                    break;
                case "overdue":
                    Add(("Не напоминать", "1"), ("Ежедневно", "0"));
                    if (profile.Mode == ReminderMode.Critical)
                        Add(("Частые повторы", "2"));
                    break;
                case "interval":
                    Add(("1 час", "60"), ("2 часа", "120"), ("3 часа", "180"));
                    break;
                case "end":
                    Add(("18:00", "18:00"), ("21:00", "21:00"), ("23:00", "23:00"));
                    break;
                default:
                    Add(("09:00", "09:00"), ("12:00", "12:00"), ("18:00", "18:00"));
                    break;
            }
            if (field is "days" or "time" or "start" or "end")
                rows.Add([DraftButton(draft, "Ввести своё", $"custom:{field}")]);
        }
        rows.Add([DraftButton(draft, "◀️ Назад", "editor"), DraftButton(draft, "Отмена", "exit")]);
        return RenderDraftAsync(client, entry, error is null ? prompt : $"{error}\n\n{prompt}", new(rows), ct);
    }

    private async Task ChangeFieldAsync(ITelegramBotClient client, User user, DraftEntry entry, string field, string value, CancellationToken ct)
    {
        var profile = entry.Draft.Profile!;
        if (!TryChangeField(profile, field, value, out var updated))
        {
            await ShowFieldAsync(client, entry, field, ct, field is "start" or "end"
                ? "Проверьте время: начало должно быть раньше окончания в пределах одного дня." : "Не получилось применить значение.");
            return;
        }
        await ShowEditorAsync(client, user, entry with { Draft = entry.Draft with { Profile = updated, Step = "editor" } }, ct);
    }

    internal static bool TryChangeField(ReminderProfile previous, string field, string value, out ReminderProfile profile)
    {
        profile = previous;
        var isNumber = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number);
        var isTime = TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time);
        if (field == "mode")
        {
            if (!isNumber || !Enum.IsDefined((ReminderMode)number))
                return false;
            if ((ReminderMode)number != previous.Mode)
                profile = ReminderProfile.DefaultFor((ReminderMode)number);
            return true;
        }
        if (!(field is "days" or "interval" or "overdue" && isNumber || field is "time" or "start" or "end" && isTime))
            return false;
        try
        {
            profile = new(previous.Mode, field == "days" ? number : previous.DaysBefore,
                field == "time" ? time : previous.ReminderTimeLocal, field == "overdue" ? (OverdueMode)number : previous.OverdueMode,
                field == "interval" ? number : previous.CriticalIntervalMinutes, field == "start" ? time : previous.CriticalWindowStart,
                field == "end" ? time : previous.CriticalWindowEnd);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task<DraftEntry?> LoadDraftAsync(Guid userId, long chatId, CancellationToken ct)
    {
        var state = await states.FindAsync(userId, ct);
        if (state?.Kind is not (ConversationKind.ReminderProfile or ConversationKind.ReminderSnooze))
            return null;
        Draft? draft = null;
        if (Now - state.UpdatedAtUtc <= DraftLifetime)
        {
            try { draft = JsonSerializer.Deserialize<Draft>(state.PayloadJson); }
            catch (JsonException) { /* A draft from a different version can be restarted. */ }
        }
        if (draft is not null)
            return draft.Screen.ChatId == chatId ? new(state, draft) : null;
        states.Remove(state);
        await states.SaveChangesAsync(ct);
        return null;
    }

    private async Task<DraftEntry> OpenDraftAsync(Guid userId, ConversationKind kind, Draft draft, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(draft);
        var state = await states.FindAsync(userId, ct);
        if (state is null)
        {
            state = ConversationState.Create(userId, kind, json, Now);
            await states.AddAsync(state, ct);
        }
        else
            state.Reset(kind, json, Now);
        await states.SaveChangesAsync(ct);
        return new(state, draft);
    }

    private async Task RenderDraftAsync(ITelegramBotClient client, DraftEntry entry, string text, InlineKeyboardMarkup keyboard, CancellationToken ct)
    {
        var screen = await ShowAsync(client, entry.Draft.Screen, text, keyboard, ct);
        entry.State.UpdatePayload(JsonSerializer.Serialize(entry.Draft with { Screen = screen }), Now);
        await states.SaveChangesAsync(ct);
    }

    private Task ClearDraftAsync(DraftEntry entry, CancellationToken ct)
    {
        states.Remove(entry.State);
        return states.SaveChangesAsync(ct);
    }

    internal static async Task<Screen> ShowAsync(ITelegramBotClient client, Screen screen, string text, InlineKeyboardMarkup keyboard, CancellationToken ct)
    {
        if (screen.MessageId is { } messageId)
        {
            try
            {
                await client.EditMessageText(screen.ChatId, messageId, text, replyMarkup: keyboard, cancellationToken: ct);
                return screen;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
            {
                return screen;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 400 &&
                (ex.Message.Contains("can't be edited", StringComparison.OrdinalIgnoreCase)
                 || ex.Message.Contains("to edit not found", StringComparison.OrdinalIgnoreCase)))
            {
                // Use the replacement message for all subsequent actions.
            }
        }
        var sent = await client.SendMessage(screen.ChatId, text, replyMarkup: keyboard, cancellationToken: ct);
        return new(screen.ChatId, sent.Id);
    }

    private static InlineKeyboardMarkup SettingsBack() => new([[Button("◀️ Настройки", TelegramCallbackData.Setting("back"))]]);
    private static InlineKeyboardMarkup ProfileBack(Guid? paymentId) =>
        new([[Button("Открыть напоминания", TelegramCallbackData.ReminderSettings(paymentId.HasValue ? "payment" : "global", paymentId))]]);
    private static InlineKeyboardButton DraftButton(Draft draft, string label, string action) =>
        Button(label, TelegramCallbackData.ReminderDraft(draft.Nonce, action));
    private static string NewNonce() => Guid.NewGuid().ToString("N")[..12];
    private static bool TryPage(string value, out int page) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out page) && page is >= 0 and <= 100_000;

    internal readonly record struct Screen(long ChatId, int? MessageId);
    private sealed record Draft(string Nonce, Screen Screen, string Step, Guid? PaymentId = null, Guid? SampleId = null,
        EffectiveReminderProfile? Original = null, ReminderProfile? Profile = null, DateOnly? DueDate = null, Guid? SnoozeId = null);
    private sealed record DraftEntry(ConversationState State, Draft Draft);
}
