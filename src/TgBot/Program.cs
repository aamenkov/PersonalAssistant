using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Exceptions;
using PersonalAssistant.Application;
using PersonalAssistant.Infrastructure;

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
builder.Services.AddScoped<IConversationStateRepository, ConversationStateRepository>();
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<PaymentConversationService>();
builder.Services.AddScoped<PaymentEditConversationService>();
builder.Services.AddScoped<PaymentRecordConversationService>();
builder.Services.AddSingleton<UserUpdateGate>();
builder.Services.AddSingleton(BotAccessPolicy.Parse(builder.Configuration["Telegram:AllowedUserIds"]));
builder.Services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(botToken));
builder.Services.AddHostedService<TelegramPollingService>();

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

        if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery?.Data is { } paymentCallback && paymentCallback.StartsWith("payment:", StringComparison.Ordinal))
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
                var response = await editor.BeginAsync(user.Id, paymentId, cancellationToken);
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, cancellationToken: cancellationToken);
                if (update.CallbackQuery.Message is { } message)
                    await client.SendMessage(message.Chat.Id, response, cancellationToken: cancellationToken);
                return;
            }

            if (parts[1] == "pay")
            {
                var recorder = scope.ServiceProvider.GetRequiredService<PaymentRecordConversationService>();
                var response = await recorder.BeginAsync(user.Id, paymentId, LocalDate(user.TimeZoneId), cancellationToken);
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, cancellationToken: cancellationToken);
                if (update.CallbackQuery.Message is { } message)
                    await client.SendMessage(message.Chat.Id, response, cancellationToken: cancellationToken);
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

            if (parts[1] == "disable-confirm")
            {
                var payments = scope.ServiceProvider.GetRequiredService<PaymentService>();
                var deactivated = await payments.DeactivateAsync(user.Id, paymentId, cancellationToken);
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, deactivated ? "Платеж отключен" : "Платеж не найден", cancellationToken: cancellationToken);
                if (update.CallbackQuery.Message is { } message)
                    await client.SendMessage(message.Chat.Id, deactivated ? "Платеж отключен. История оплат сохранена." : "Платеж не найден или уже отключен.", cancellationToken: cancellationToken);
                return;
            }

            await client.AnswerCallbackQuery(update.CallbackQuery.Id, "Действие отменено", cancellationToken: cancellationToken);
            return;
        }

        if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery?.Data is { } callbackData && callbackData.StartsWith("timezone:", StringComparison.Ordinal))
        {
            var timeZoneId = callbackData["timezone:".Length..];
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

        if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery?.Data is { } settingsData && settingsData.StartsWith("settings:", StringComparison.Ordinal))
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
                    await settings.SetDefaultCurrencyAsync(update.CallbackQuery.From.Id, parts[2], cancellationToken);
                    await client.AnswerCallbackQuery(update.CallbackQuery.Id, $"Валюта по умолчанию: {parts[2]}", cancellationToken: cancellationToken);
                }
                else if (parts[1] == "days" && parts.Length == 3 && int.TryParse(parts[2], out var days))
                {
                    var user = await settings.FindAsync(update.CallbackQuery.From.Id, cancellationToken)
                        ?? throw new InvalidOperationException("User is not registered.");
                    await settings.SetReminderSettingsAsync(update.CallbackQuery.From.Id, user.ReminderTimeLocal, days, cancellationToken);
                    await client.AnswerCallbackQuery(update.CallbackQuery.Id, $"Напоминание за {days} дн.", cancellationToken: cancellationToken);
                }
                else if (parts[1] == "time" && parts.Length == 4 && TimeOnly.TryParse($"{parts[2]}:{parts[3]}", out var reminderTime))
                {
                    await settings.SetReminderTimeAsync(update.CallbackQuery.From.Id, reminderTime, cancellationToken);
                    await client.AnswerCallbackQuery(update.CallbackQuery.Id, $"Время напоминаний: {reminderTime:HH\\:mm}", cancellationToken: cancellationToken);
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
        else if (command is "/payments" or "/upcoming")
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
            var isUpcoming = command == "/upcoming";
            var today = LocalDate(user.TimeZoneId);
            var items = await payments.GetActiveAsync(user.Id, isUpcoming ? today : null, isUpcoming ? today.AddDays(6) : null, cancellationToken);
            await client.SendMessage(update.Message.Chat.Id, FormatPayments(items, isUpcoming), cancellationToken: cancellationToken);
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

            await client.SendMessage(update.Message.Chat.Id, "Настройки профиля. Часовой пояс можно изменить здесь, а валюту и напоминания — кнопками ниже:",
                replyMarkup: SettingsKeyboard(), cancellationToken: cancellationToken);
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
                await client.SendMessage(update.Message.Chat.Id, response, replyMarkup: AddStepKeyboard(response), cancellationToken: cancellationToken);
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

    private static InlineKeyboardMarkup TimeZoneKeyboard() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("UTC (UTC+0)", "timezone:UTC"), InlineKeyboardButton.WithCallbackData("Москва (UTC+3)", "timezone:Europe/Moscow") },
        new[] { InlineKeyboardButton.WithCallbackData("Берлин (UTC+1)", "timezone:Europe/Berlin"), InlineKeyboardButton.WithCallbackData("Алматы (UTC+5)", "timezone:Asia/Almaty") },
        new[] { InlineKeyboardButton.WithCallbackData("Токио (UTC+9)", "timezone:Asia/Tokyo"), InlineKeyboardButton.WithCallbackData("Нью-Йорк (UTC−5)", "timezone:America/New_York") }
    });

    private static InlineKeyboardMarkup SettingsKeyboard() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("Изменить часовой пояс", "settings:timezone") },
        new[] { InlineKeyboardButton.WithCallbackData("Валюта: RUB", "settings:currency:RUB"), InlineKeyboardButton.WithCallbackData("USD", "settings:currency:USD") },
        new[] { InlineKeyboardButton.WithCallbackData("Напоминать за 1 день", "settings:days:1"), InlineKeyboardButton.WithCallbackData("за 3 дня", "settings:days:3") },
        new[] { InlineKeyboardButton.WithCallbackData("Напоминать за 7 дней", "settings:days:7") },
        new[] { InlineKeyboardButton.WithCallbackData("Время: 09:00", "settings:time:09:00"), InlineKeyboardButton.WithCallbackData("12:00", "settings:time:12:00") },
        new[] { InlineKeyboardButton.WithCallbackData("18:00", "settings:time:18:00") }
    });

    private static ReplyKeyboardMarkup MainMenuKeyboard() => new(new[]
    {
        new KeyboardButton[] { "Предстоящие платежи", "Мои платежи" },
        new KeyboardButton[] { "Добавить платеж", "Отметить оплату" },
        new KeyboardButton[] { "Статистика", "История" },
        new KeyboardButton[] { "Настройки", "Помощь" }
    }) { ResizeKeyboard = true };

    private static ReplyKeyboardMarkup? AddStepKeyboard(string response)
    {
        if (response.Contains("периодичность", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Еженедельно", "Ежемесячно" }, new KeyboardButton[] { "Ежегодно", "Однократно" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("дату", StringComparison.OrdinalIgnoreCase) || response.Contains("Сегодня", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Сегодня" }, new KeyboardButton[] { "Отмена" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("Сохранить", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Да", "Нет" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("отмена", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Отмена" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        return null;
    }

    private static DateOnly LocalDate(string timeZoneId)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone));
    }

    private static string FormatPayments(IReadOnlyList<PaymentListItem> payments, bool upcoming)
    {
        if (payments.Count == 0)
            return upcoming ? "На ближайшие 7 дней платежей нет." : "Активных платежей пока нет.";

        var title = upcoming ? "Предстоящие платежи:" : "Активные платежи:";
        var lines = payments.Select(x => $"• {x.Name} — {x.Amount:0.##} {x.Currency}, {x.DueDate:yyyy-MM-dd} ({x.RecurrenceUnit})");
        return title + "\n" + string.Join("\n", lines);
    }

    private static InlineKeyboardMarkup PaymentActionKeyboard(IReadOnlyList<PaymentListItem> payments, string action) =>
        new(payments.Select(payment => new[]
        {
            InlineKeyboardButton.WithCallbackData($"{payment.Name} — {payment.Amount:0.##} {payment.Currency}", $"payment:{action}:{payment.Id}")
        }));

    private static string FormatHistory(IReadOnlyList<PaymentTransactionItem> history)
    {
        if (history.Count == 0)
            return "История оплат пока пуста.";

        var lines = history.Select(x => $"• {x.PaidDate:yyyy-MM-dd} — {x.PaymentName}: {x.PaidAmount:0.##} {x.Currency} (период {x.PaidPeriod})");
        return "История оплат:\n" + string.Join("\n", lines);
    }

    private static string? GetCommand(string text)
    {
        return text.Trim() switch
        {
            "Предстоящие платежи" => "/upcoming",
            "Мои платежи" => "/payments",
            "Добавить платеж" => "/add",
            "Отметить оплату" => "/pay",
            "Статистика" => "/stats",
            "История" => "/history",
            "Настройки" => "/settings",
            "Помощь" => "/help",
            _ => GetSlashCommand(text)
        };
    }

    private static string? GetSlashCommand(string text)
    {
        var token = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null || !token.StartsWith('/'))
            return null;

        return token.Split('@')[0].ToLowerInvariant();
    }

    private static bool TryParseMonth(string command, string timeZoneId, out int year, out int month)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && command is "Статистика" or "История")
        {
            var localDate = LocalDate(timeZoneId);
            year = localDate.Year;
            month = localDate.Month;
            return true;
        }
        if (parts.Length == 1)
        {
            var localDate = LocalDate(timeZoneId);
            year = localDate.Year;
            month = localDate.Month;
            return true;
        }

        if (parts.Length == 2)
            return UserInputParser.TryParseYearMonth(parts[1], out year, out month);

        year = 0;
        month = 0;
        return false;
    }

    private static string FormatStatistics(int year, int month, IReadOnlyList<MonthlyStatisticsCurrency> statistics)
    {
        if (statistics.Count == 0)
            return $"Статистика за {year:D4}-{month:D2}: платежей и оплат нет.";

        var lines = statistics.Select(x =>
            $"{x.Currency}:\n" +
            $"  Запланировано: {x.PlannedAmount:0.##} ({x.PlannedCount})\n" +
            $"  Оплачено: {x.PaidAmount:0.##} ({x.PaidCount})\n" +
            $"  Осталось: {x.RemainingAmount:0.##}\n" +
            $"  Не оплачено платежей: {x.UnpaidCount}");
        return $"Статистика за {year:D4}-{month:D2}:\n" + string.Join("\n", lines);
    }
}
