using PersonalAssistant.Application;
using PersonalAssistant.Domain;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.ReplyMarkups;

namespace PersonalAssistant.Bot;

internal sealed class ReminderBackgroundService(
    ReminderService reminders,
    ITelegramBotClient bot,
    ILogger<ReminderBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Reminder worker started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var notifications = await reminders.GetDueAsync(DateTime.UtcNow, stoppingToken);
                foreach (var notification in notifications)
                {
                    try
                    {
                        await bot.SendMessage(notification.ChatId, Format(notification),
                            replyMarkup: new InlineKeyboardMarkup(new[]
                            {
                                new[] { InlineKeyboardButton.WithCallbackData("✅ Оплатил", TelegramCallbackData.Payment("pay", notification.PaymentId)) }
                            }), cancellationToken: stoppingToken);
                    }
                    catch (RequestException exception)
                    {
                        logger.LogWarning("Reminder delivery failed for payment {PaymentId}: {Message}", notification.PaymentId, exception.Message);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Reminder worker iteration failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private static string Format(ReminderNotification notification)
    {
        var amount = TelegramPresentation.Money(notification.Amount, notification.Currency);
        if (notification.Kind == ReminderKind.Overdue)
        {
            return $"⚠️ Платеж просрочен\n\n{notification.Name}\n{amount}\n\nСрок оплаты был {TelegramPresentation.RelativeDays(notification.DaysUntilDue)}.";
        }

        if (notification.Kind == ReminderKind.DueToday)
        {
            var prefix = notification.IsAutoDebit ? "Сегодня ожидается автоматическое списание:" : "Сегодня нужно оплатить:";
            return $"🔔 Сегодня платеж\n\n{prefix}\n{notification.Name}\n{amount}";
        }

        var reminderPrefix = notification.IsAutoDebit ? "Через" : "Через";
        return $"🔔 Скоро платеж\n\n{reminderPrefix} {notification.DaysUntilDue} дн. нужно оплатить:\n\n{notification.Name}\n{amount}\n📅 до {notification.DueDate:dd.MM.yyyy}";
    }
}
