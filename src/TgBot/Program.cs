using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Exceptions;
using PersonalAssistant.Application;
using PersonalAssistant.Infrastructure;
using PersonalAssistant.Bot;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole();
var botToken = builder.Configuration["Telegram:BotToken"];
if (string.IsNullOrWhiteSpace(botToken))
    throw new InvalidOperationException("Telegram:BotToken is not configured.");

builder.Services.AddPersonalAssistantInfrastructure(builder.Configuration);
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<UserRegistrationService>();
builder.Services.AddScoped<UserTimeZoneService>();
builder.Services.AddScoped<UserSettingsService>();
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<IReminderRepository, ReminderRepository>();
builder.Services.AddScoped<ITelegramUpdateStore, TelegramUpdateStore>();
builder.Services.AddScoped<ReminderService>();
builder.Services.AddScoped<ReminderSnoozeService>();
builder.Services.AddScoped<IConversationStateRepository, ConversationStateRepository>();
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<PaymentConversationService>();
builder.Services.AddScoped<PaymentEditConversationService>();
builder.Services.AddScoped<PaymentRecordConversationService>();
builder.Services.AddSingleton<UserUpdateGate>();
builder.Services.AddSingleton(BotAccessPolicy.Parse(builder.Configuration["Telegram:AllowedUserIds"]));
builder.Services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(botToken));
builder.Services.AddHostedService<TelegramPollingService>();
builder.Services.AddHostedService<ReminderBackgroundService>();

var app = builder.Build();
app.MapGet("/health", async (PersonalAssistantDbContext db, CancellationToken cancellationToken) =>
{
    try
    {
        return await db.Database.CanConnectAsync(cancellationToken)
            ? Results.Ok(new { status = "ok" })
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    catch
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});
await ApplyMigrationsAsync(app);
await app.RunAsync();

static async Task ApplyMigrationsAsync(IHost app)
{
    using var scope = app.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseMigration");
    var db = scope.ServiceProvider.GetRequiredService<PersonalAssistantDbContext>();
    logger.LogInformation("Applying database migrations");
    await db.Database.OpenConnectionAsync();
    try
    {
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(hashtext('PersonalAssistant:migrations'))");
        await db.Database.MigrateAsync();
        logger.LogInformation("Database migrations applied");
    }
    finally
    {
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(hashtext('PersonalAssistant:migrations'))");
        await db.Database.CloseConnectionAsync();
    }
}

internal sealed class TelegramPollingService(
    ITelegramBotClient bot,
    IServiceScopeFactory scopeFactory,
    UserUpdateGate updateGate,
    BotAccessPolicy accessPolicy,
    ILogger<TelegramPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, new ReceiverOptions { DropPendingUpdates = false }, stoppingToken);
        logger.LogInformation("PersonalAssistant polling started");
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken cancellationToken)
    {
        var telegramUserId = update.CallbackQuery?.From.Id ?? update.Message?.From?.Id;
        if (telegramUserId.HasValue && !accessPolicy.IsAllowed(telegramUserId.Value))
        {
            logger.LogWarning("Rejected Telegram update from unauthorized user");
            if (update.CallbackQuery is { } unauthorizedCallback)
                await client.AnswerCallbackQuery(unauthorizedCallback.Id, "Доступ к боту ограничен.", showAlert: true, cancellationToken: cancellationToken);
            else if (update.Message is { } unauthorizedMessage)
                await client.SendMessage(unauthorizedMessage.Chat.Id, "Доступ к боту ограничен.", cancellationToken: cancellationToken);
            return;
        }

        if (telegramUserId is null)
            return;

        await updateGate.RunAsync(telegramUserId.Value, async () =>
        {
            using var updateScope = scopeFactory.CreateScope();
            var updateStore = updateScope.ServiceProvider.GetRequiredService<ITelegramUpdateStore>();
            if (!await updateStore.TryBeginAsync(update.Id, DateTime.UtcNow, cancellationToken))
            {
                logger.LogInformation("Skipped already processed Telegram update {UpdateId}", update.Id);
                return;
            }

            try
            {
                await HandleUpdateCoreAsync(client, update, cancellationToken);
                await updateStore.CompleteAsync(update.Id, DateTime.UtcNow, cancellationToken);
            }
            catch (Exception exception)
            {
                await updateStore.AbandonAsync(update.Id, cancellationToken);
                if (exception is RequestException)
                    logger.LogWarning("Telegram API temporarily unavailable while responding to update {UpdateId}: {Message}", update.Id, exception.Message);
                else
                    logger.LogError(exception, "Telegram update processing failed for update {UpdateId}", update.Id);
            }
        }, cancellationToken);
    }

    private async Task HandleUpdateCoreAsync(ITelegramBotClient client, Update update, CancellationToken cancellationToken)
    {
        if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery?.Data is { } snoozeData
            && snoozeData.StartsWith(TelegramCallbackData.ReminderPrefix + "snooze:", StringComparison.Ordinal))
        {
            var parts = snoozeData.Split(':');
            if (parts.Length is < 3 or > 4 || !Guid.TryParse(parts[2], out var snoozePaymentId))
            {
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, "Некорректное напоминание", showAlert: true, cancellationToken: cancellationToken);
                return;
            }

            using var snoozeScope = scopeFactory.CreateScope();
            var snoozeUser = await snoozeScope.ServiceProvider.GetRequiredService<IUserRepository>()
                .FindByTelegramUserIdAsync(update.CallbackQuery.From.Id, cancellationToken);
            if (snoozeUser is null)
            {
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, "Сначала выполните /start", showAlert: true, cancellationToken: cancellationToken);
                return;
            }

            if (parts.Length == 3)
            {
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, cancellationToken: cancellationToken);
                if (update.CallbackQuery.Message is { } snoozeMessage)
                    await client.SendMessage(snoozeMessage.Chat.Id, "Когда напомнить еще раз?",
                        replyMarkup: TelegramUi.ReminderSnoozeKeyboard(snoozePaymentId), cancellationToken: cancellationToken);
                return;
            }

            var option = parts[3].ToLowerInvariant() switch
            {
                "hour" => ReminderSnoozeOption.InOneHour,
                "evening" => ReminderSnoozeOption.ThisEvening,
                "tomorrow" => ReminderSnoozeOption.Tomorrow,
                _ => (ReminderSnoozeOption?)null
            };
            if (!option.HasValue)
            {
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, "Некорректный вариант", showAlert: true, cancellationToken: cancellationToken);
                return;
            }

            var snoozeResult = await snoozeScope.ServiceProvider.GetRequiredService<ReminderSnoozeService>()
                .SnoozeAsync(snoozeUser, snoozePaymentId, option.Value, DateTime.UtcNow, cancellationToken);
            await client.AnswerCallbackQuery(update.CallbackQuery.Id,
                snoozeResult.Succeeded ? "Напоминание отложено" : "Платеж уже недоступен", cancellationToken: cancellationToken);
            if (snoozeResult.Succeeded && update.CallbackQuery.Message is { } resultMessage)
            {
                var timeZone = TimeZoneInfo.FindSystemTimeZoneById(snoozeUser.TimeZoneId);
                var localUntil = TimeZoneInfo.ConvertTimeFromUtc(snoozeResult.SnoozedUntilUtc, timeZone);
                await client.SendMessage(resultMessage.Chat.Id, $"⏰ Хорошо, напомню {localUntil:dd.MM.yyyy в HH\\:mm}.",
                    replyMarkup: TelegramUi.MainMenuKeyboard(), cancellationToken: cancellationToken);
            }
            return;
        }

        if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery?.Data is { } paymentCallback && paymentCallback.StartsWith(TelegramCallbackData.PaymentPrefix, StringComparison.Ordinal))
        {
            using var scope = scopeFactory.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var user = await users.FindByTelegramUserIdAsync(update.CallbackQuery.From.Id, cancellationToken);
            if (user is null)
            {
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, "Сначала выполните /start", showAlert: true, cancellationToken: cancellationToken);
                return;
            }

            var parts = paymentCallback.Split(':');
            if (parts.Length != 3 || !Guid.TryParse(parts[2], out var paymentId))
            {
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, "Некорректный платеж", showAlert: true, cancellationToken: cancellationToken);
                return;
            }

            if (parts[1] == "edit")
            {
                var editor = scope.ServiceProvider.GetRequiredService<PaymentEditConversationService>();
                var response = await editor.BeginAsync(user.Id, paymentId, LocalDate(user.TimeZoneId), cancellationToken);
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, cancellationToken: cancellationToken);
                if (update.CallbackQuery.Message is { } message)
                    await client.SendMessage(message.Chat.Id, response, replyMarkup: TelegramUi.EditFieldsKeyboard(paymentId), cancellationToken: cancellationToken);
                return;
            }

            if (parts[1] == "pay")
            {
                var recorder = scope.ServiceProvider.GetRequiredService<PaymentRecordConversationService>();
                var response = await recorder.BeginAsync(user.Id, paymentId, LocalDate(user.TimeZoneId), cancellationToken);
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, cancellationToken: cancellationToken);
                if (update.CallbackQuery.Message is { } message)
                    await client.SendMessage(message.Chat.Id, response, replyMarkup: ConversationKeyboard(response), cancellationToken: cancellationToken);
                return;
            }

            if (parts[1] == "disable")
            {
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, "Подтвердите отключение", cancellationToken: cancellationToken);
                if (update.CallbackQuery.Message is { } message)
                    await client.SendMessage(message.Chat.Id, "Отключить этот платеж? История оплат сохранится.",
                        replyMarkup: new InlineKeyboardMarkup(new[]
                        {
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("Да, отключить", $"payment:disable-confirm:{paymentId}"),
                                InlineKeyboardButton.WithCallbackData("Отмена", $"payment:disable-cancel:{Guid.Empty}")
                            }
                        }), cancellationToken: cancellationToken);
                return;
            }

            if (parts[1] == "more")
            {
                var details = await scope.ServiceProvider.GetRequiredService<PaymentService>().GetDetailsAsync(user.Id, paymentId, cancellationToken);
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, cancellationToken: cancellationToken);
                if (update.CallbackQuery.Message is { } message)
                    await client.SendMessage(message.Chat.Id, $"Дополнительные действия для {details?.Name ?? "платежа"}:",
                        replyMarkup: new InlineKeyboardMarkup(new[]
                        {
                            new[] { InlineKeyboardButton.WithCallbackData("⏸ Отключить", TelegramCallbackData.Payment("disable", paymentId)) },
                            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", TelegramCallbackData.Payment("back", paymentId)) }
                        }), cancellationToken: cancellationToken);
                return;
            }

            if (parts[1] == "back")
            {
                var details = await scope.ServiceProvider.GetRequiredService<PaymentService>().GetDetailsAsync(user.Id, paymentId, cancellationToken);
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, cancellationToken: cancellationToken);
                if (details is not null && update.CallbackQuery.Message is { } message)
                {
                    var item = new PaymentListItem(details.Id, details.Name, details.Amount, details.Currency, details.NextPaymentDate,
                        details.RecurrenceInterval, details.RecurrenceUnit, details.PaymentMethod, details.IsAutoDebit);
                    await client.SendMessage(message.Chat.Id, TelegramUi.FormatPaymentCard(item, LocalDate(user.TimeZoneId)),
                        replyMarkup: TelegramUi.PaymentCardKeyboard(paymentId), cancellationToken: cancellationToken);
                }
                return;
            }

            if (parts[1] == "disable-confirm")
            {
                var payments = scope.ServiceProvider.GetRequiredService<PaymentService>();
                var deactivated = await payments.DeactivateAsync(user.Id, paymentId, cancellationToken);
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, deactivated ? "Платеж отключен" : "Платеж не найден", cancellationToken: cancellationToken);
                if (update.CallbackQuery.Message is { } message)
                    await client.SendMessage(message.Chat.Id, deactivated ? "Платеж отключен. История оплат сохранена." : "Платеж не найден или уже отключен.", replyMarkup: MainMenuKeyboard(), cancellationToken: cancellationToken);
                return;
            }

            await client.AnswerCallbackQuery(update.CallbackQuery.Id, "Действие отменено", cancellationToken: cancellationToken);
            return;
        }

        if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery?.Data is { } editData && editData.StartsWith(TelegramCallbackData.EditPrefix, StringComparison.Ordinal))
        {
            var parts = editData.Split(':');
            if (parts.Length != 3 || !Guid.TryParse(parts[2], out var paymentId))
            {
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, "Некорректное действие", showAlert: true, cancellationToken: cancellationToken);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var user = await users.FindByTelegramUserIdAsync(update.CallbackQuery.From.Id, cancellationToken);
            if (user is null)
            {
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, "Сначала выполните /start", showAlert: true, cancellationToken: cancellationToken);
                return;
            }

            var editor = scope.ServiceProvider.GetRequiredService<PaymentEditConversationService>();
            var response = await editor.BeginFieldAsync(user.Id, paymentId, parts[1], LocalDate(user.TimeZoneId), cancellationToken);
            await client.AnswerCallbackQuery(update.CallbackQuery.Id, cancellationToken: cancellationToken);
            if (update.CallbackQuery.Message is { } message)
                await client.SendMessage(message.Chat.Id, response, replyMarkup: ConversationKeyboard(response), cancellationToken: cancellationToken);
            return;
        }

        if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery?.Data is { } monthData &&
            (monthData.StartsWith("stats:month:", StringComparison.Ordinal) || monthData.StartsWith("history:month:", StringComparison.Ordinal)))
        {
            var parts = monthData.Split(':');
            if (parts.Length != 3 || !DateTime.TryParseExact(parts[2], "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var selectedMonth))
            {
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, "Некорректный месяц", showAlert: true, cancellationToken: cancellationToken);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var user = await users.FindByTelegramUserIdAsync(update.CallbackQuery.From.Id, cancellationToken);
            if (user is null)
            {
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, "Сначала выполните /start", showAlert: true, cancellationToken: cancellationToken);
                return;
            }

            var payments = scope.ServiceProvider.GetRequiredService<PaymentService>();
            string response;
            if (parts[0] == "stats")
            {
                var statistics = await payments.GetMonthlyStatisticsAsync(user.Id, selectedMonth.Year, selectedMonth.Month, cancellationToken);
                var annual = await payments.GetAnnualStatisticsAsync(user.Id, selectedMonth.Year, cancellationToken);
                response = TelegramUi.FormatStatistics(selectedMonth.Year, selectedMonth.Month, statistics, annual);
            }
            else
            {
                var start = new DateOnly(selectedMonth.Year, selectedMonth.Month, 1);
                var history = await payments.GetHistoryAsync(user.Id, null, start, start.AddMonths(1).AddDays(-1), cancellationToken);
                response = TelegramUi.FormatHistory(selectedMonth.Year, selectedMonth.Month, history);
            }

            await client.AnswerCallbackQuery(update.CallbackQuery.Id, cancellationToken: cancellationToken);
            if (update.CallbackQuery.Message is { } message)
                await client.SendMessage(message.Chat.Id, response,
                    replyMarkup: TelegramUi.MonthNavigationKeyboard(parts[0] + ":", selectedMonth.Year, selectedMonth.Month), cancellationToken: cancellationToken);
            return;
        }

        if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery?.Data is { } callbackData && callbackData.StartsWith(TelegramCallbackData.TimeZonePrefix, StringComparison.Ordinal))
        {
            var timeZoneId = callbackData[TelegramCallbackData.TimeZonePrefix.Length..];
            using var scope = scopeFactory.CreateScope();
            var timeZones = scope.ServiceProvider.GetRequiredService<UserTimeZoneService>();
            try
            {
                await timeZones.SetAsync(update.CallbackQuery.From.Id, timeZoneId, cancellationToken);
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, "Часовой пояс сохранен", cancellationToken: cancellationToken);
                if (update.CallbackQuery.Message is { } callbackMessage)
                    await client.SendMessage(callbackMessage.Chat.Id, $"Готово. Часовой пояс: {TelegramPresentation.TimeZone(timeZoneId)}.", replyMarkup: MainMenuKeyboard(), cancellationToken: cancellationToken);
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, "Неизвестный часовой пояс", showAlert: true, cancellationToken: cancellationToken);
            }
            catch (InvalidOperationException)
            {
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, "Сначала выполните /start", showAlert: true, cancellationToken: cancellationToken);
            }

            return;
        }

        if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery?.Data is { } settingsData && settingsData.StartsWith(TelegramCallbackData.SettingsPrefix, StringComparison.Ordinal))
        {
            var parts = settingsData.Split(':');
            using var scope = scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<UserSettingsService>();
            try
            {
                if (parts[1] == "timezone")
                {
                    await client.AnswerCallbackQuery(update.CallbackQuery.Id, cancellationToken: cancellationToken);
                    if (update.CallbackQuery.Message is { } message)
                        await client.SendMessage(message.Chat.Id, "Выберите часовой пояс. Его можно изменить в любой момент:", replyMarkup: TimeZoneKeyboard(), cancellationToken: cancellationToken);
                }
                else if (parts[1] == "currency" && parts.Length == 3)
                {
                    if (parts[2] == "menu")
                    {
                        await client.AnswerCallbackQuery(update.CallbackQuery.Id, cancellationToken: cancellationToken);
                        if (update.CallbackQuery.Message is { } message)
                            await client.SendMessage(message.Chat.Id, "Выберите валюту по умолчанию:", replyMarkup: CurrencyKeyboard(), cancellationToken: cancellationToken);
                        return;
                    }

                    await settings.SetDefaultCurrencyAsync(update.CallbackQuery.From.Id, parts[2], cancellationToken);
                    await client.AnswerCallbackQuery(update.CallbackQuery.Id, $"Валюта по умолчанию: {parts[2]}", cancellationToken: cancellationToken);
                    if (update.CallbackQuery.Message is { } currencyMessage)
                    {
                        var updatedUser = await settings.FindAsync(update.CallbackQuery.From.Id, cancellationToken);
                        await client.SendMessage(currencyMessage.Chat.Id, "Настройка сохранена.", replyMarkup: SettingsKeyboard(updatedUser?.DefaultCurrency ?? parts[2]), cancellationToken: cancellationToken);
                    }
                }
                else if (parts[1] == "days" && parts.Length == 3 && parts[2] == "menu")
                {
                    await client.AnswerCallbackQuery(update.CallbackQuery.Id, cancellationToken: cancellationToken);
                    if (update.CallbackQuery.Message is { } message)
                        await client.SendMessage(message.Chat.Id, "Выберите, за сколько дней напоминать:", replyMarkup: ReminderDaysKeyboard(), cancellationToken: cancellationToken);
                    return;
                }
                else if (parts[1] == "days" && parts.Length == 3 && int.TryParse(parts[2], out var days))
                {
                    var user = await settings.FindAsync(update.CallbackQuery.From.Id, cancellationToken)
                        ?? throw new InvalidOperationException("User is not registered.");
                    await settings.SetReminderSettingsAsync(update.CallbackQuery.From.Id, user.ReminderTimeLocal, days, cancellationToken);
                    await client.AnswerCallbackQuery(update.CallbackQuery.Id, $"Напоминание за {days} дн.", cancellationToken: cancellationToken);
                    if (update.CallbackQuery.Message is { } daysMessage)
                    {
                        var updatedUser = await settings.FindAsync(update.CallbackQuery.From.Id, cancellationToken);
                        await client.SendMessage(daysMessage.Chat.Id, "Настройка сохранена.", replyMarkup: SettingsKeyboard(updatedUser?.DefaultCurrency ?? "RUB"), cancellationToken: cancellationToken);
                    }
                }
                else if (parts[1] == "time" && parts.Length == 3 && parts[2] == "menu")
                {
                    await client.AnswerCallbackQuery(update.CallbackQuery.Id, cancellationToken: cancellationToken);
                    if (update.CallbackQuery.Message is { } message)
                        await client.SendMessage(message.Chat.Id, "Выберите время напоминаний:", replyMarkup: ReminderTimeKeyboard(), cancellationToken: cancellationToken);
                    return;
                }
                else if (parts[1] == "time" && parts.Length == 4 && TimeOnly.TryParse($"{parts[2]}:{parts[3]}", out var reminderTime))
                {
                    await settings.SetReminderTimeAsync(update.CallbackQuery.From.Id, reminderTime, cancellationToken);
                    await client.AnswerCallbackQuery(update.CallbackQuery.Id, $"Время напоминаний: {reminderTime:HH\\:mm}", cancellationToken: cancellationToken);
                    if (update.CallbackQuery.Message is { } timeMessage)
                    {
                        var updatedUser = await settings.FindAsync(update.CallbackQuery.From.Id, cancellationToken);
                        await client.SendMessage(timeMessage.Chat.Id, "Настройка сохранена.", replyMarkup: SettingsKeyboard(updatedUser?.DefaultCurrency ?? "RUB"), cancellationToken: cancellationToken);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, "Сначала выполните /start", showAlert: true, cancellationToken: cancellationToken);
            }

            return;
        }

        if (update.Type != UpdateType.Message || update.Message?.Text is not { } text || update.Message.From is not { } from)
            return;

        var command = GetCommand(text);
        if (command == "/start")
        {
            using var scope = scopeFactory.CreateScope();
            var registration = scope.ServiceProvider.GetRequiredService<UserRegistrationService>();
            var user = await registration.RegisterOrUpdateAsync(from.Id, update.Message.Chat.Id, from.FirstName, from.Username, cancellationToken);
            if (user.IsTimeZoneConfigured)
            {
                var today = LocalDate(user.TimeZoneId);
                var upcoming = await scope.ServiceProvider.GetRequiredService<PaymentService>().GetUpcomingAsync(user.Id, today, 6, cancellationToken);
                var first = upcoming.FirstOrDefault();
                var summary = first is null
                    ? "На ближайшие 7 дней платить ничего не нужно 👍"
                    : first.IsOverdue
                        ? $"⚠️ Есть просроченный платеж:\n\n{first.Name} — {TelegramPresentation.Money(first.Amount, first.Currency)}\nСрок был {TelegramPresentation.Date(first.DueDate, today)}"
                        : $"Ближайший платеж:\n\n{first.Name} — {TelegramPresentation.Money(first.Amount, first.Currency)}\n{TelegramPresentation.Date(first.DueDate, today, true)} · {TelegramPresentation.RelativeDays(first.DaysFromToday)}";
                await client.SendMessage(update.Message.Chat.Id, $"С возвращением 👋\n\n{summary}", replyMarkup: MainMenuKeyboard(), cancellationToken: cancellationToken);
            }
            else
                await client.SendMessage(update.Message.Chat.Id,
                    "Добро пожаловать в PersonalAssistant!\n\nЧтобы напоминания приходили по вашему местному времени, выберите часовой пояс. Его можно изменить позже в настройках:",
                    replyMarkup: TimeZoneKeyboard(), cancellationToken: cancellationToken);
        }
        else if (command == "/add")
        {
            using var scope = scopeFactory.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var user = await users.FindByTelegramUserIdAsync(from.Id, cancellationToken);
            if (user is null)
            {
                await client.SendMessage(update.Message.Chat.Id, "Сначала выполните /start.", cancellationToken: cancellationToken);
                return;
            }

            var conversations = scope.ServiceProvider.GetRequiredService<PaymentConversationService>();
            await client.SendMessage(update.Message.Chat.Id, await conversations.BeginAsync(user.Id, user.DefaultCurrency, LocalDate(user.TimeZoneId), cancellationToken), replyMarkup: AddStepKeyboard("Введите название платежа:"), cancellationToken: cancellationToken);
        }
        else if (command == "/upcoming")
        {
            using var scope = scopeFactory.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var user = await users.FindByTelegramUserIdAsync(from.Id, cancellationToken);
            if (user is null)
            {
                await client.SendMessage(update.Message.Chat.Id, "Сначала выполните /start.", cancellationToken: cancellationToken);
                return;
            }

            var today = LocalDate(user.TimeZoneId);
            var payments = scope.ServiceProvider.GetRequiredService<PaymentService>();
            var items = await payments.GetUpcomingAsync(user.Id, today, 6, cancellationToken);
            if (items.Count == 0)
            {
                await client.SendMessage(update.Message.Chat.Id, "💳 Ближайшие платежи\n\nНа ближайшие 7 дней платить ничего не нужно 👍", replyMarkup: MainMenuKeyboard(), cancellationToken: cancellationToken);
                return;
            }

            await client.SendMessage(update.Message.Chat.Id, "💳 Ближайшие платежи", cancellationToken: cancellationToken);
            foreach (var item in items)
                await client.SendMessage(update.Message.Chat.Id, TelegramUi.FormatUpcomingCard(item, today),
                    replyMarkup: TelegramUi.PaymentCardKeyboard(item.Id), cancellationToken: cancellationToken);
        }
        else if (command == "/payments")
        {
            using var scope = scopeFactory.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var user = await users.FindByTelegramUserIdAsync(from.Id, cancellationToken);
            if (user is null)
            {
                await client.SendMessage(update.Message.Chat.Id, "Сначала выполните /start.", cancellationToken: cancellationToken);
                return;
            }

            var payments = scope.ServiceProvider.GetRequiredService<PaymentService>();
            var items = await payments.GetActiveAsync(user.Id, null, null, cancellationToken);
            if (items.Count == 0)
            {
                await client.SendMessage(update.Message.Chat.Id, "📋 Активных платежей пока нет.", replyMarkup: MainMenuKeyboard(), cancellationToken: cancellationToken);
                return;
            }

            var today = LocalDate(user.TimeZoneId);
            await client.SendMessage(update.Message.Chat.Id, $"📋 Активные платежи — {items.Count}", cancellationToken: cancellationToken);
            foreach (var item in items)
                await client.SendMessage(update.Message.Chat.Id, TelegramUi.FormatPaymentCard(item, today),
                    replyMarkup: TelegramUi.PaymentCardKeyboard(item.Id), cancellationToken: cancellationToken);
        }
        else if (command is "/edit" or "/disable" or "/pay")
        {
            using var scope = scopeFactory.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var user = await users.FindByTelegramUserIdAsync(from.Id, cancellationToken);
            if (user is null)
            {
                await client.SendMessage(update.Message.Chat.Id, "Сначала выполните /start.", cancellationToken: cancellationToken);
                return;
            }

            var payments = scope.ServiceProvider.GetRequiredService<PaymentService>();
            var items = await payments.GetActiveAsync(user.Id, null, null, cancellationToken);
            var isEdit = command == "/edit";
            var isPay = command == "/pay";
            if (items.Count == 0)
            {
                await client.SendMessage(update.Message.Chat.Id, "Активных платежей пока нет.", cancellationToken: cancellationToken);
                return;
            }

            var action = isEdit ? "edit" : isPay ? "pay" : "disable";
            var prompt = isEdit ? "Выберите платеж для редактирования:" : isPay ? "Выберите оплаченный платеж:" : "Выберите платеж для отключения:";
            await client.SendMessage(update.Message.Chat.Id, prompt,
                replyMarkup: PaymentActionKeyboard(items, action), cancellationToken: cancellationToken);
        }
        else if (command == "/history")
        {
            using var scope = scopeFactory.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var user = await users.FindByTelegramUserIdAsync(from.Id, cancellationToken);
            if (user is null)
            {
                await client.SendMessage(update.Message.Chat.Id, "Сначала выполните /start.", cancellationToken: cancellationToken);
                return;
            }

            var payments = scope.ServiceProvider.GetRequiredService<PaymentService>();
            var monthCommand = text.StartsWith("/", StringComparison.Ordinal) ? text : command;
            if (!TryParseMonth(monthCommand, user.TimeZoneId, out var year, out var month))
            {
                await client.SendMessage(update.Message.Chat.Id, "Укажите месяц в формате `/history ГГГГ-ММ`, например `/history 2026-08`.", cancellationToken: cancellationToken);
                return;
            }
            var startDate = new DateOnly(year, month, 1);
            var endDate = startDate.AddMonths(1).AddDays(-1);
            var history = await payments.GetHistoryAsync(user.Id, null, startDate, endDate, cancellationToken);
            await client.SendMessage(update.Message.Chat.Id, TelegramUi.FormatHistory(year, month, history),
                replyMarkup: TelegramUi.MonthNavigationKeyboard("history:", year, month), cancellationToken: cancellationToken);
        }
        else if (command == "/stats")
        {
            using var scope = scopeFactory.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var user = await users.FindByTelegramUserIdAsync(from.Id, cancellationToken);
            if (user is null)
            {
                await client.SendMessage(update.Message.Chat.Id, "Сначала выполните /start.", cancellationToken: cancellationToken);
                return;
            }

            var monthCommand = text.StartsWith("/", StringComparison.Ordinal) ? text : command;
            if (!TryParseMonth(monthCommand, user.TimeZoneId, out var year, out var month))
            {
                await client.SendMessage(update.Message.Chat.Id, "Укажите месяц в формате `/stats ГГГГ-ММ`, например `/stats 2026-08`.", cancellationToken: cancellationToken);
                return;
            }
            var payments = scope.ServiceProvider.GetRequiredService<PaymentService>();
            var statistics = await payments.GetMonthlyStatisticsAsync(user.Id, year, month, cancellationToken);
            var annual = await payments.GetAnnualStatisticsAsync(user.Id, year, cancellationToken);
            await client.SendMessage(update.Message.Chat.Id, FormatStatistics(year, month, statistics, annual),
                replyMarkup: TelegramUi.MonthNavigationKeyboard("stats:", year, month), cancellationToken: cancellationToken);
        }
        else if (command == "/settings")
        {
            using var scope = scopeFactory.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            if (await users.FindByTelegramUserIdAsync(from.Id, cancellationToken) is null)
            {
                await client.SendMessage(update.Message.Chat.Id, "Сначала выполните /start.", cancellationToken: cancellationToken);
                return;
            }

            var user = await users.FindByTelegramUserIdAsync(from.Id, cancellationToken);
            await client.SendMessage(update.Message.Chat.Id,
                $"⚙️ Настройки\n\nЧасовой пояс: {TelegramPresentation.TimeZone(user!.TimeZoneId)}\nВалюта: {user.DefaultCurrency}\nНапоминание: за {user.ReminderDaysBefore} дн.\nВремя: {user.ReminderTimeLocal:HH\\:mm}\n\nВыберите параметр для изменения:",
                replyMarkup: SettingsKeyboard(user.DefaultCurrency, user.TimeZoneId, user.ReminderDaysBefore, user.ReminderTimeLocal), cancellationToken: cancellationToken);
        }
        else if (command == "/help")
        {
            await client.SendMessage(update.Message.Chat.Id, "Доступные команды:\n/start — регистрация и выбор часового пояса\n/settings — изменить часовой пояс\n/add — добавить платеж\n/payments — все активные платежи\n/upcoming — платежи на ближайшие 7 дней\n/edit — изменить платеж\n/disable — отключить платеж\n/pay — отметить оплату\n/history [YYYY-MM] — история оплат\n/stats [YYYY-MM] — статистика месяца\n/help — справка", cancellationToken: cancellationToken);
        }
        else
        {
            using var scope = scopeFactory.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var user = await users.FindByTelegramUserIdAsync(from.Id, cancellationToken);
            if (user is null)
            {
                await client.SendMessage(update.Message.Chat.Id, "Сначала выполните /start.", cancellationToken: cancellationToken);
                return;
            }

            var response = await scope.ServiceProvider.GetRequiredService<PaymentConversationService>().HandleInputAsync(user.Id, text, cancellationToken)
                ?? await scope.ServiceProvider.GetRequiredService<PaymentEditConversationService>().HandleInputAsync(user.Id, text, cancellationToken)
                ?? await scope.ServiceProvider.GetRequiredService<PaymentRecordConversationService>().HandleInputAsync(user.Id, text, cancellationToken);
            if (response is not null)
            {
                var finished = response.Contains("отменено", StringComparison.OrdinalIgnoreCase)
                    || response.Contains("сохранен", StringComparison.OrdinalIgnoreCase)
                    || response.Contains("обновлен", StringComparison.OrdinalIgnoreCase);
                await client.SendMessage(update.Message.Chat.Id, response,
                    replyMarkup: finished ? MainMenuKeyboard() : ConversationKeyboard(response), cancellationToken: cancellationToken);
            }
            else
                await client.SendMessage(update.Message.Chat.Id, "Команда не распознана. Используйте /help.", cancellationToken: cancellationToken);
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient client, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is RequestException)
            logger.LogWarning("Telegram API temporarily unavailable: {Message}", exception.Message);
        else
            logger.LogError(exception, "Telegram update processing failed");
        return Task.CompletedTask;
    }

    private static InlineKeyboardMarkup TimeZoneKeyboard() => TelegramUi.TimeZoneKeyboard();

    private static InlineKeyboardMarkup SettingsKeyboard(string currency, string timeZoneId = "UTC", int reminderDays = 3, TimeOnly? reminderTime = null) => TelegramUi.SettingsKeyboard(currency, timeZoneId, reminderDays, reminderTime);

    private static InlineKeyboardMarkup ReminderDaysKeyboard() => TelegramUi.ReminderDaysKeyboard();

    private static InlineKeyboardMarkup ReminderTimeKeyboard() => TelegramUi.ReminderTimeKeyboard();

    private static InlineKeyboardMarkup CurrencyKeyboard() => TelegramUi.CurrencyKeyboard();

    private static ReplyKeyboardMarkup MainMenuKeyboard() => TelegramUi.MainMenuKeyboard();

    private static ReplyKeyboardMarkup? AddStepKeyboard(string response) => TelegramUi.ConversationKeyboard(response);

    private static ReplyKeyboardMarkup? ConversationKeyboard(string response) => TelegramUi.ConversationKeyboard(response);

    private static DateOnly LocalDate(string timeZoneId) => TelegramUi.LocalDate(timeZoneId);

    private static string FormatPayments(IReadOnlyList<PaymentListItem> payments, bool upcoming) => TelegramUi.FormatPayments(payments, upcoming);

    private static InlineKeyboardMarkup PaymentActionKeyboard(IReadOnlyList<PaymentListItem> payments, string action) => TelegramUi.PaymentActionKeyboard(payments, action);

    private static string FormatHistory(IReadOnlyList<PaymentTransactionItem> history) => TelegramUi.FormatHistory(history);

    private static string? GetCommand(string text) => TelegramUi.GetCommand(text);

    private static string? GetSlashCommand(string text) => TelegramUi.GetCommand(text);

    private static bool TryParseMonth(string command, string timeZoneId, out int year, out int month) => TelegramUi.TryParseMonth(command, timeZoneId, out year, out month);

    private static string FormatStatistics(int year, int month, IReadOnlyList<MonthlyStatisticsCurrency> statistics, IReadOnlyList<AnnualStatisticsCurrency> annual) => TelegramUi.FormatStatistics(year, month, statistics, annual);
}
