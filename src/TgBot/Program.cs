using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Exceptions;
using PersonalAssistant.Application;
using PersonalAssistant.Infrastructure;
using PersonalAssistant.Bot;

var builder = Host.CreateApplicationBuilder(args);
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
builder.Services.AddScoped<ReminderService>();
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
await ApplyMigrationsAsync(app);
await app.RunAsync();

static async Task ApplyMigrationsAsync(IHost app)
{
    using var scope = app.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseMigration");
    logger.LogInformation("Applying database migrations");
    await scope.ServiceProvider.GetRequiredService<PersonalAssistantDbContext>().Database.MigrateAsync();
    logger.LogInformation("Database migrations applied");
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
            try
            {
                await HandleUpdateCoreAsync(client, update, cancellationToken);
            }
            catch (RequestException exception)
            {
                logger.LogWarning("Telegram API temporarily unavailable while responding to update {UpdateId}: {Message}", update.Id, exception.Message);
            }
        }, cancellationToken);
    }

    private async Task HandleUpdateCoreAsync(ITelegramBotClient client, Update update, CancellationToken cancellationToken)
    {

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
                    await client.SendMessage(message.Chat.Id, response, replyMarkup: ConversationKeyboard(response), cancellationToken: cancellationToken);
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
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, cancellationToken: cancellationToken);
                if (update.CallbackQuery.Message is { } message)
                    await client.SendMessage(message.Chat.Id, "Дополнительные действия:",
                        replyMarkup: new InlineKeyboardMarkup(new[]
                        {
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("⏸ Отключить", TelegramCallbackData.Payment("disable", paymentId))
                            }
                        }), cancellationToken: cancellationToken);
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
                    await client.SendMessage(callbackMessage.Chat.Id, $"Готово. Часовой пояс: {timeZoneId}.", replyMarkup: MainMenuKeyboard(), cancellationToken: cancellationToken);
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
                await client.SendMessage(update.Message.Chat.Id, $"С возвращением! Ваш часовой пояс: {user.TimeZoneId}.", replyMarkup: MainMenuKeyboard(), cancellationToken: cancellationToken);
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
            await client.SendMessage(update.Message.Chat.Id, TelegramUi.FormatUpcoming(items, today, 6),
                replyMarkup: TelegramUi.UpcomingKeyboard(items), cancellationToken: cancellationToken);
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
            await client.SendMessage(update.Message.Chat.Id, FormatPayments(items, false),
                replyMarkup: TelegramUi.PaymentOverviewKeyboard(items), cancellationToken: cancellationToken);
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
            if (!TryParseMonth(text, user.TimeZoneId, out var year, out var month))
            {
                await client.SendMessage(update.Message.Chat.Id, "Укажите месяц в формате `/history ГГГГ-ММ`, например `/history 2026-08`.", cancellationToken: cancellationToken);
                return;
            }
            var startDate = new DateOnly(year, month, 1);
            var endDate = startDate.AddMonths(1).AddDays(-1);
            var history = await payments.GetHistoryAsync(user.Id, null, startDate, endDate, cancellationToken);
            await client.SendMessage(update.Message.Chat.Id, FormatHistory(history), cancellationToken: cancellationToken);
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

            if (!TryParseMonth(text, user.TimeZoneId, out var year, out var month))
            {
                await client.SendMessage(update.Message.Chat.Id, "Укажите месяц в формате `/stats ГГГГ-ММ`, например `/stats 2026-08`.", cancellationToken: cancellationToken);
                return;
            }
            var payments = scope.ServiceProvider.GetRequiredService<PaymentService>();
            var statistics = await payments.GetMonthlyStatisticsAsync(user.Id, year, month, cancellationToken);
            await client.SendMessage(update.Message.Chat.Id, FormatStatistics(year, month, statistics), cancellationToken: cancellationToken);
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
            await client.SendMessage(update.Message.Chat.Id, "Настройки профиля. Выберите параметр для изменения:",
                replyMarkup: SettingsKeyboard(user!.DefaultCurrency), cancellationToken: cancellationToken);
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

    private static InlineKeyboardMarkup SettingsKeyboard(string currency) => TelegramUi.SettingsKeyboard(currency);

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

    private static string FormatStatistics(int year, int month, IReadOnlyList<MonthlyStatisticsCurrency> statistics) => TelegramUi.FormatStatistics(year, month, statistics);
}
