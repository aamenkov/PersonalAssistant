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
        new KeyboardButton[] { "💳 Ближайшие платежи", "➕ Добавить платеж" },
        new KeyboardButton[] { "📋 Все платежи", "📊 Статистика" },
        new KeyboardButton[] { "⚙️ Настройки" }
    }) { ResizeKeyboard = true };

    public static ReplyKeyboardMarkup? ConversationKeyboard(string response) =>
        IsCompleted(response) ? MainMenuKeyboard() : AddStepKeyboard(response);

    public static string FormatPayments(IReadOnlyList<PaymentListItem> payments, bool upcoming)
    {
        if (payments.Count == 0)
            return upcoming ? "На ближайшие 7 дней платежей нет." : "Активных платежей пока нет.";

        var title = upcoming ? "💳 Предстоящие платежи:" : $"📋 Активные платежи — {payments.Count}";
        var lines = payments.Select(x =>
            $"{x.Name}\n{TelegramPresentation.Money(x.Amount, x.Currency)} · {PaymentDisplayNames.Recurrence(x.RecurrenceUnit)}\nСледующий: {TelegramPresentation.Date(x.DueDate, DateOnly.MinValue)}");
        return title + "\n" + string.Join("\n", lines);
    }

    public static string FormatUpcoming(IReadOnlyList<UpcomingPaymentItem> payments, DateOnly today, int windowDays)
    {
        if (payments.Count == 0)
            return "💳 Ближайшие платежи\n\nАктивных платежей пока нет.";

        var visible = payments.Where(x => x.IsOverdue || x.DueDate <= today.AddDays(windowDays)).ToList();
        var next = payments.FirstOrDefault(x => !x.IsOverdue && x.DueDate > today.AddDays(windowDays));
        var sections = new List<string> { "💳 Ближайшие платежи" };
        if (visible.Count == 0)
            sections.Add("На ближайшие 7 дней платить ничего не нужно 👍");

        foreach (var payment in visible)
        {
            var status = payment.IsOverdue
                ? $"⚠️ ПРОСРОЧЕНО НА {Math.Abs(payment.DaysFromToday)} дн.\nНужно было оплатить до {TelegramPresentation.Date(payment.DueDate, today)}"
                : $"📅 {TelegramPresentation.Date(payment.DueDate, today)} · {TelegramPresentation.RelativeDays(payment.DaysFromToday)}";
            sections.Add($"{(payment.IsOverdue ? "⚠️" : "💳")} {payment.Name}\n{TelegramPresentation.Money(payment.Amount, payment.Currency)}\n{status}");
        }

        if (next is not null)
            sections.Add($"Следующий платеж:\n\n💳 {next.Name}\n{TelegramPresentation.Money(next.Amount, next.Currency)}\n📅 {TelegramPresentation.Date(next.DueDate, today, true)} · {TelegramPresentation.RelativeDays(next.DaysFromToday)}");

        return string.Join("\n\n", sections);
    }

    public static InlineKeyboardMarkup PaymentActionKeyboard(IReadOnlyList<PaymentListItem> payments, string action) =>
        new(payments.Select(payment => new[]
        {
            InlineKeyboardButton.WithCallbackData($"{ActionLabel(action)}: {payment.Name} — {payment.Amount:0.##} {payment.Currency}", TelegramCallbackData.Payment(action, payment.Id))
        }));

    public static InlineKeyboardMarkup PaymentOverviewKeyboard(IReadOnlyList<PaymentListItem> payments) =>
        new(payments.SelectMany(payment => new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ Оплатил", TelegramCallbackData.Payment("pay", payment.Id)),
                InlineKeyboardButton.WithCallbackData("✏️ Изменить", TelegramCallbackData.Payment("edit", payment.Id)),
                InlineKeyboardButton.WithCallbackData("⋯", TelegramCallbackData.Payment("more", payment.Id))
            }
        }));

    public static InlineKeyboardMarkup UpcomingKeyboard(IReadOnlyList<UpcomingPaymentItem> payments) =>
        new(payments.Select(payment => new[]
        {
            InlineKeyboardButton.WithCallbackData($"✅ Оплатил: {payment.Name}", TelegramCallbackData.Payment("pay", payment.Id))
        }));

    public static string FormatHistory(IReadOnlyList<PaymentTransactionItem> history)
    {
        if (history.Count == 0)
            return "История оплат пока пуста.";

        var lines = history.Select(x =>
            $"✅ {x.PaymentName} — {TelegramPresentation.Money(x.PaidAmount, x.Currency)}\n" +
            $"Оплачено {TelegramPresentation.Date(x.PaidDate, DateOnly.MinValue)}\n" +
            $"Платеж за {PaymentMonth(x.PaidPeriod)}");
        return "📜 История оплат:\n\n" + string.Join("\n\n", lines);
    }

    public static string FormatStatistics(int year, int month, IReadOnlyList<MonthlyStatisticsCurrency> statistics)
    {
        if (statistics.Count == 0)
            return $"Статистика за {year:D4}-{month:D2}: платежей и оплат нет.";

        var title = new DateOnly(year, month, 1).ToDateTime(TimeOnly.MinValue)
            .ToString("MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        var lines = statistics.Select(x =>
            $"📊 {title}\n\n" +
            $"Запланировано: {TelegramPresentation.Money(x.PlannedAmount, x.Currency)}\n" +
            $"✅ Оплачено: {TelegramPresentation.Money(x.PaidAmount, x.Currency)}\n" +
            $"⏳ Осталось: {TelegramPresentation.Money(x.RemainingAmount, x.Currency)}\n\n" +
            (x.UnpaidCount == 0 ? "Все платежи оплачены 🎉" : $"Не оплачено платежей: {x.UnpaidCount}"));
        return string.Join("\n\n", lines);
    }

    public static string? GetCommand(string text) => text.Trim() switch
    {
        "💳 Ближайшие платежи" => "/upcoming",
        "➕ Добавить платеж" => "/add",
        "📋 Все платежи" => "/payments",
        "📊 Статистика" => "/stats",
        "⚙️ Настройки" => "/settings",
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
        if (response.Contains("Сохранить", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Да", "Нет" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("Ожидаемая сумма", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Ожидаемая сумма" }, new KeyboardButton[] { "Отмена" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("Без комментария", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Без комментария" }, new KeyboardButton[] { "Отмена" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("периодичность", StringComparison.OrdinalIgnoreCase))
        {
            var rows = new List<KeyboardButton[]>
            {
                new KeyboardButton[] { "Еженедельно", "Ежемесячно" },
                new KeyboardButton[] { "Ежегодно", "Однократно" }
            };
            if (response.Contains("Оставить текущее", StringComparison.OrdinalIgnoreCase))
                rows.Add(new KeyboardButton[] { "Оставить текущее" });
            return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true, OneTimeKeyboard = true };
        }
        if (response.Contains("способ оплаты", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Карта", "Банковский перевод" }, new KeyboardButton[] { "Наличные", "Другое" }, new KeyboardButton[] { "Оставить текущий" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("автосписание", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Да", "Нет" }, new KeyboardButton[] { "Оставить текущее" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("дату", StringComparison.OrdinalIgnoreCase) || response.Contains("Сегодня", StringComparison.OrdinalIgnoreCase))
        {
            var rows = new List<KeyboardButton[]>
            {
                new KeyboardButton[] { "Сегодня", "Завтра" },
                new KeyboardButton[] { "Первое число следующего месяца" },
                new KeyboardButton[] { "То же число следующего месяца" }
            };
            if (response.Contains("Оставить текущее", StringComparison.OrdinalIgnoreCase))
                rows.Add(new KeyboardButton[] { "Оставить текущее" });
            rows.Add(new KeyboardButton[] { "Отмена" });
            return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true, OneTimeKeyboard = true };
        }
        if (response.Contains("Оставить текущее", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Оставить текущее" }, new KeyboardButton[] { "Отмена" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("отмена", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Отмена" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        return null;
    }

    private static string ActionLabel(string action) => action switch
    {
        "disable" => "⏸ Отключить",
        "pay" => "✅ Оплатить",
        "edit" => "✏️ Изменить",
        _ => "Открыть"
    };

    private static bool IsCompleted(string response) =>
        response.Contains("сохранен", StringComparison.OrdinalIgnoreCase)
        || response.Contains("обновлен", StringComparison.OrdinalIgnoreCase)
        || response.Contains("отменено", StringComparison.OrdinalIgnoreCase)
        || response.Contains("отменена", StringComparison.OrdinalIgnoreCase)
        || response.Contains("отменены", StringComparison.OrdinalIgnoreCase);

    private static string PaymentMonth(string paidPeriod)
    {
        return DateOnly.TryParseExact(paidPeriod, "yyyy-MM-dd", out var date)
            ? date.ToDateTime(TimeOnly.MinValue).ToString("MMMM", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))
            : "указанный период";
    }

    private static string? GetSlashCommand(string text)
    {
        var token = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null || !token.StartsWith('/'))
            return null;
        return token.Split('@')[0].ToLowerInvariant();
    }
}
