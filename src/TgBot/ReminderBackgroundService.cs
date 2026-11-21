using PersonalAssistant.Application;
using PersonalAssistant.Domain;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.ReplyMarkups;

namespace PersonalAssistant.Bot;

internal sealed class ReminderBackgroundService(
    IServiceScopeFactory scopeFactory,
    [FromKeyedServices(ReminderBackgroundService.BotClientKey)] ITelegramBotClient bot,
    ILogger<ReminderBackgroundService> logger) : BackgroundService
{
    internal const string BotClientKey = "reminder-delivery";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Reminder worker started");
        using var ticker = new PeriodicTimer(TimeSpan.FromMinutes(1));
        if (stoppingToken.IsCancellationRequested)
            return;
        do
        {
            try
            {
                await RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Reminder worker iteration failed");
            }
        }
        while (await ticker.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunPassAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var reminders = scope.ServiceProvider.GetRequiredService<ReminderService>();
        foreach (var dispatch in await reminders.GetDueAsync(cancellationToken))
        {
            try
            {
                var reservation = await reminders.TryBeginAsync(dispatch, cancellationToken);
                if (reservation is null)
                    continue;

                var result = await SendAsync(dispatch, cancellationToken);
                await reminders.CompleteAsync(dispatch, reservation, result, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to process reminder for payment {PaymentId}", dispatch.Payment.PaymentId);
            }
        }
    }

    private async Task<ReminderSendResult> SendAsync(ReminderDispatch dispatch, CancellationToken cancellationToken)
    {
        var payment = dispatch.Payment;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var message = await bot.SendMessage(payment.ChatId, Format(dispatch),
                replyMarkup: new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("✅ Оплатил", TelegramCallbackData.Period("p", payment.PaymentId, dispatch.Key.DueDate)) },
                    new[] { InlineKeyboardButton.WithCallbackData("⏰ Напомнить позже", TelegramCallbackData.Period("s", payment.PaymentId, dispatch.Key.DueDate)) }
                }), cancellationToken: timeout.Token);
            return new ReminderSendResult(ReminderSendOutcome.Sent, message.Id);
        }
        catch (ApiRequestException exception) when (exception.ErrorCode == 429)
        {
            logger.LogWarning("Telegram rate limit for payment {PaymentId}", payment.PaymentId);
            return new ReminderSendResult(ReminderSendOutcome.Rejected,
                RetryAfter: TimeSpan.FromSeconds(exception.Parameters?.RetryAfter ?? 60));
        }
        catch (ApiRequestException exception) when (exception.ErrorCode is >= 400 and < 500 and not 408)
        {
            logger.LogWarning("Telegram rejected reminder for payment {PaymentId} with code {Code}", payment.PaymentId, exception.ErrorCode);
            return new ReminderSendResult(ReminderSendOutcome.Rejected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Reminder outcome unknown for payment {PaymentId} ({ErrorType})", payment.PaymentId, exception.GetType().Name);
            return new ReminderSendResult(ReminderSendOutcome.Unknown);
        }
    }

    private static string Format(ReminderDispatch dispatch)
    {
        var payment = dispatch.Payment;
        var amount = TelegramPresentation.Money(payment.Amount, payment.Currency);
        if (dispatch.Key.Kind == ReminderKind.Snoozed)
            return $"⏰ Напоминание\n\n{payment.Name}\n{amount}\n\nПора проверить и оплатить платеж.";
        if (dispatch.Key.Kind == ReminderKind.Overdue)
        {
            return $"⚠️ Платеж просрочен\n\n{payment.Name}\n{amount}\n\nСрок оплаты был {TelegramPresentation.RelativeDays(dispatch.DaysUntilDue)}.";
        }

        if (dispatch.Key.Kind == ReminderKind.DueToday)
        {
            var prefix = payment.IsAutoDebit ? "Сегодня ожидается автоматическое списание:" : "Сегодня нужно оплатить:";
            return $"🔔 Сегодня платеж\n\n{prefix}\n{payment.Name}\n{amount}";
        }

        return $"🔔 Скоро платеж\n\nЧерез {dispatch.DaysUntilDue} дн. нужно оплатить:\n\n{payment.Name}\n{amount}\n📅 до {payment.DueDate:dd.MM.yyyy}";
    }
}
