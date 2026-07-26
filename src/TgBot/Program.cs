using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using PersonalAssistant.Application;
using PersonalAssistant.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=personalassistant;Username=personalassistant;Password=change-me";

builder.Services.AddDbContext<PersonalAssistantDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<UserRegistrationService>();
builder.Services.AddScoped<UserTimeZoneService>();
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<IConversationStateRepository, ConversationStateRepository>();
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<PaymentConversationService>();
builder.Services.AddSingleton<ITelegramBotClient>(_ =>
    new TelegramBotClient(builder.Configuration["Telegram:BotToken"]
        ?? throw new InvalidOperationException("Telegram:BotToken is not configured.")));
builder.Services.AddHostedService<TelegramPollingService>();

await builder.Build().RunAsync();

internal sealed class TelegramPollingService(
    ITelegramBotClient bot,
    IServiceScopeFactory scopeFactory,
    ILogger<TelegramPollingService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, new ReceiverOptions { DropPendingUpdates = true }, stoppingToken);
        logger.LogInformation("PersonalAssistant polling started");
        return Task.CompletedTask;
    }

    private async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken cancellationToken)
    {
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
                    await client.SendMessage(callbackMessage.Chat.Id, $"Готово. Часовой пояс: {timeZoneId}.", cancellationToken: cancellationToken);
            }
            catch (TimeZoneNotFoundException)
            {
                await client.AnswerCallbackQuery(update.CallbackQuery.Id, "Неизвестный часовой пояс", showAlert: true, cancellationToken: cancellationToken);
            }

            return;
        }

        if (update.Type != UpdateType.Message || update.Message?.Text is not { } text || update.Message.From is not { } from)
            return;

        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            using var scope = scopeFactory.CreateScope();
            var registration = scope.ServiceProvider.GetRequiredService<UserRegistrationService>();
            await registration.RegisterOrUpdateAsync(from.Id, update.Message.Chat.Id, from.FirstName, from.Username, cancellationToken);
            await client.SendMessage(update.Message.Chat.Id,
                "Добро пожаловать в PersonalAssistant!\n\nЧтобы напоминания приходили по вашему местному времени, выберите часовой пояс:",
                replyMarkup: TimeZoneKeyboard(), cancellationToken: cancellationToken);
        }
        else if (text.StartsWith("/add", StringComparison.OrdinalIgnoreCase))
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
            await client.SendMessage(update.Message.Chat.Id, await conversations.BeginAsync(user.Id, cancellationToken), cancellationToken: cancellationToken);
        }
        else if (text.StartsWith("/payments", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/upcoming", StringComparison.OrdinalIgnoreCase))
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
            var isUpcoming = text.StartsWith("/upcoming", StringComparison.OrdinalIgnoreCase);
            var today = LocalDate(user.TimeZoneId);
            var items = await payments.GetActiveAsync(user.Id, isUpcoming ? today : null, isUpcoming ? today.AddDays(7) : null, cancellationToken);
            await client.SendMessage(update.Message.Chat.Id, FormatPayments(items, isUpcoming), cancellationToken: cancellationToken);
        }
        else if (text.StartsWith("/settings", StringComparison.OrdinalIgnoreCase))
        {
            await client.SendMessage(update.Message.Chat.Id, "Выберите часовой пояс для напоминаний:",
                replyMarkup: TimeZoneKeyboard(), cancellationToken: cancellationToken);
        }
        else if (text.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
        {
            await client.SendMessage(update.Message.Chat.Id, "Доступные команды:\n/start — регистрация и выбор часового пояса\n/settings — изменить часовой пояс\n/add — добавить платеж\n/payments — все активные платежи\n/upcoming — платежи на ближайшие 7 дней\n/help — справка", cancellationToken: cancellationToken);
        }
        else
        {
            using var scope = scopeFactory.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var user = await users.FindByTelegramUserIdAsync(from.Id, cancellationToken);
            if (user is null)
                return;

            var conversations = scope.ServiceProvider.GetRequiredService<PaymentConversationService>();
            var response = await conversations.HandleInputAsync(user.Id, text, cancellationToken);
            if (response is not null)
                await client.SendMessage(update.Message.Chat.Id, response, cancellationToken: cancellationToken);
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient client, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Telegram update processing failed");
        return Task.CompletedTask;
    }

    private static InlineKeyboardMarkup TimeZoneKeyboard() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("UTC", "timezone:UTC"), InlineKeyboardButton.WithCallbackData("Москва (UTC+3)", "timezone:Europe/Moscow") },
        new[] { InlineKeyboardButton.WithCallbackData("Берлин", "timezone:Europe/Berlin"), InlineKeyboardButton.WithCallbackData("Алматы", "timezone:Asia/Almaty") },
        new[] { InlineKeyboardButton.WithCallbackData("Токио", "timezone:Asia/Tokyo"), InlineKeyboardButton.WithCallbackData("Нью-Йорк", "timezone:America/New_York") }
    });

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
}
