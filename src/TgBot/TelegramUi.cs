using System.Globalization;
using PersonalAssistant.Application;
using Telegram.Bot.Types.ReplyMarkups;

namespace PersonalAssistant.Bot;

internal static class TelegramUi
{
    public static InlineKeyboardMarkup TimeZoneKeyboard() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("UTC (UTC+0)", TelegramCallbackData.TimeZone("UTC")), InlineKeyboardButton.WithCallbackData("Москва (UTC+3)", TelegramCallbackData.TimeZone("Europe/Moscow")) },
        new[] { InlineKeyboardButton.WithCallbackData("Берлин (UTC+1)", TelegramCallbackData.TimeZone("Europe/Berlin")), InlineKeyboardButton.WithCallbackData("Алматы (UTC+5)", TelegramCallbackData.TimeZone("Asia/Almaty")) },
        new[] { InlineKeyboardButton.WithCallbackData("Токио (UTC+9)", TelegramCallbackData.TimeZone("Asia/Tokyo")), InlineKeyboardButton.WithCallbackData("Нью-Йорк (UTC−5)", TelegramCallbackData.TimeZone("America/New_York")) }
    });

    public static InlineKeyboardMarkup SettingsKeyboard(string currency) => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("Изменить часовой пояс", TelegramCallbackData.Setting("timezone")) },
        new[] { InlineKeyboardButton.WithCallbackData($"Валюта ({currency})", TelegramCallbackData.Setting("currency", "menu")) },
        new[] { InlineKeyboardButton.WithCallbackData("Дни до напоминания", TelegramCallbackData.Setting("days", "menu")) },
        new[] { InlineKeyboardButton.WithCallbackData("Время напоминания", TelegramCallbackData.Setting("time", "menu")) }
    });

    public static InlineKeyboardMarkup ReminderDaysKeyboard() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("За 1 день", TelegramCallbackData.Setting("days", 1)), InlineKeyboardButton.WithCallbackData("За 3 дня", TelegramCallbackData.Setting("days", 3)) },
        new[] { InlineKeyboardButton.WithCallbackData("За 7 дней", TelegramCallbackData.Setting("days", 7)) }
    });

    public static InlineKeyboardMarkup ReminderTimeKeyboard() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("09:00", TelegramCallbackData.Setting("time", "09:00")), InlineKeyboardButton.WithCallbackData("12:00", TelegramCallbackData.Setting("time", "12:00")) },
        new[] { InlineKeyboardButton.WithCallbackData("18:00", TelegramCallbackData.Setting("time", "18:00")) }
    });

    public static InlineKeyboardMarkup CurrencyKeyboard() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("Российский рубль (RUB)", TelegramCallbackData.Setting("currency", "RUB")) },
        new[] { InlineKeyboardButton.WithCallbackData("Доллар США (USD)", TelegramCallbackData.Setting("currency", "USD")) },
        new[] { InlineKeyboardButton.WithCallbackData("Евро (EUR)", TelegramCallbackData.Setting("currency", "EUR")) }
    });

    public static ReplyKeyboardMarkup MainMenuKeyboard() => new(new[]
    {
        new KeyboardButton[] { "Предстоящие платежи", "Мои платежи" },
        new KeyboardButton[] { "Добавить платеж", "Отметить оплату" },
        new KeyboardButton[] { "Статистика", "История" },
        new KeyboardButton[] { "Настройки", "Помощь" }
    }) { ResizeKeyboard = true };

    public static ReplyKeyboardMarkup? ConversationKeyboard(string response) =>
        IsCompleted(response) ? MainMenuKeyboard() : AddStepKeyboard(response);

    public static string FormatPayments(IReadOnlyList<PaymentListItem> payments, bool upcoming)
    {
        if (payments.Count == 0)
            return upcoming ? "На ближайшие 7 дней платежей нет." : "Активных платежей пока нет.";

        var title = upcoming ? "Предстоящие платежи:" : "Активные платежи:";
        var lines = payments.Select(x => $"• {x.Name} — {x.Amount:0.##} {x.Currency}, {x.DueDate:dd.MM.yyyy} ({PaymentDisplayNames.Recurrence(x.RecurrenceUnit)})");
        return title + "\n" + string.Join("\n", lines);
    }

    public static InlineKeyboardMarkup PaymentActionKeyboard(IReadOnlyList<PaymentListItem> payments, string action) =>
        new(payments.Select(payment => new[]
        {
            InlineKeyboardButton.WithCallbackData($"{payment.Name} — {payment.Amount:0.##} {payment.Currency}", TelegramCallbackData.Payment(action, payment.Id))
        }));

    public static string FormatHistory(IReadOnlyList<PaymentTransactionItem> history)
    {
        if (history.Count == 0)
            return "История оплат пока пуста.";

        var lines = history.Select(x => $"• {x.PaidDate:dd.MM.yyyy} — {x.PaymentName}: {x.PaidAmount:0.##} {x.Currency} (период {x.PaidPeriod})");
        return "История оплат:\n" + string.Join("\n", lines);
    }

    public static string FormatStatistics(int year, int month, IReadOnlyList<MonthlyStatisticsCurrency> statistics)
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

    public static string? GetCommand(string text) => text.Trim() switch
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

    public static bool TryParseMonth(string command, string timeZoneId, out int year, out int month)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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

    public static DateOnly LocalDate(string timeZoneId)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone));
    }

    private static ReplyKeyboardMarkup? AddStepKeyboard(string response)
    {
        if (response.Contains("периодичность", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Еженедельно", "Ежемесячно" }, new KeyboardButton[] { "Ежегодно", "Однократно" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("способ оплаты", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Карта", "Банковский перевод" }, new KeyboardButton[] { "Наличные", "Другое" }, new KeyboardButton[] { "Оставить текущий" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("автосписание", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Да", "Нет" }, new KeyboardButton[] { "Оставить текущее" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("дату", StringComparison.OrdinalIgnoreCase) || response.Contains("Сегодня", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Сегодня", "Завтра" }, new KeyboardButton[] { "Первое число следующего месяца" }, new KeyboardButton[] { "То же число следующего месяца" }, new KeyboardButton[] { "Отмена" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("Сохранить", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Да", "Нет" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("отмена", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Отмена" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        return null;
    }

    private static bool IsCompleted(string response) =>
        response.Contains("сохранен", StringComparison.OrdinalIgnoreCase)
        || response.Contains("обновлен", StringComparison.OrdinalIgnoreCase)
        || response.Contains("отменено", StringComparison.OrdinalIgnoreCase)
        || response.Contains("отменена", StringComparison.OrdinalIgnoreCase)
        || response.Contains("отменены", StringComparison.OrdinalIgnoreCase);

    private static string? GetSlashCommand(string text)
    {
        var token = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null || !token.StartsWith('/'))
            return null;
        return token.Split('@')[0].ToLowerInvariant();
    }
}
