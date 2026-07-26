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
        else if (text.StartsWith("/settings", StringComparison.OrdinalIgnoreCase))
        {
            await client.SendMessage(update.Message.Chat.Id, "Выберите часовой пояс для напоминаний:",
                replyMarkup: TimeZoneKeyboard(), cancellationToken: cancellationToken);
        }
        else if (text.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
        {
            await client.SendMessage(update.Message.Chat.Id, "Доступные команды:\n/start — регистрация и выбор часового пояса\n/settings — изменить часовой пояс\n/help — справка\n\nУчет платежей будет добавлен на следующем этапе.", cancellationToken: cancellationToken);
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
}
