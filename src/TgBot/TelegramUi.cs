using System.Globalization;
using PersonalAssistant.Application;
using PersonalAssistant.Domain;
using Telegram.Bot.Types.ReplyMarkups;

namespace PersonalAssistant.Bot;

internal static class TelegramUi
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static InlineKeyboardMarkup TimeZoneKeyboard(bool fromSettings = false)
    {
        InlineKeyboardButton Zone(string id) =>
            InlineKeyboardButton.WithCallbackData(TelegramPresentation.TimeZone(id), TelegramCallbackData.TimeZone(id, fromSettings));

        InlineKeyboardButton[][] rows =
        [
            [Zone("UTC"), Zone("Europe/Moscow")],
            [Zone("Europe/Berlin"), Zone("Asia/Almaty")],
            [Zone("Asia/Tokyo"), Zone("America/New_York")],
            [InlineKeyboardButton.WithCallbackData("🕒 Указать текущее время", TelegramCallbackData.TimeZone("custom", fromSettings))]
        ];
        return fromSettings ? SettingsOptions(rows) : new InlineKeyboardMarkup(rows);
    }

    public static InlineKeyboardMarkup SettingsKeyboard() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("Часовой пояс", TelegramCallbackData.Setting("timezone")), InlineKeyboardButton.WithCallbackData("Валюта", TelegramCallbackData.Setting("currency", "menu")) },
        new[] { InlineKeyboardButton.WithCallbackData("🔔 Напоминания", TelegramCallbackData.ReminderSettings("global")) }
    });

    public static string FormatSettings(User user) =>
        $"⚙️ Настройки\n\nЧасовой пояс: {TelegramPresentation.TimeZone(user.TimeZoneId)}\n" +
        $"Валюта: {user.DefaultCurrency}\nНапоминания: {ReminderTelegramUi.ModeName(user.ReminderMode)}";

    private static InlineKeyboardMarkup SettingsOptions(InlineKeyboardButton[][] rows) =>
        new([.. rows, [InlineKeyboardButton.WithCallbackData("◀️ Назад", TelegramCallbackData.Setting("back"))]]);

    public static InlineKeyboardMarkup CurrencyKeyboard() => SettingsOptions(
    [
        [InlineKeyboardButton.WithCallbackData("Российский рубль (RUB)", TelegramCallbackData.Setting("currency", "RUB"))],
        [InlineKeyboardButton.WithCallbackData("Доллар США (USD)", TelegramCallbackData.Setting("currency", "USD"))],
        [InlineKeyboardButton.WithCallbackData("Евро (EUR)", TelegramCallbackData.Setting("currency", "EUR"))]
    ]);

    public static ReplyKeyboardMarkup MainMenuKeyboard(bool isAdmin = false)
    {
        var rows = new List<KeyboardButton[]>
        {
            new KeyboardButton[] { "💳 Ближайшие платежи", "➕ Добавить платеж" },
            new KeyboardButton[] { "📋 Все платежи", "📊 Статистика" },
            new KeyboardButton[] { "⚙️ Настройки" }
        };
        if (isAdmin)
            rows.Add(new KeyboardButton[] { "🛠 Админская панель" });
        return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };
    }

    public static InlineKeyboardMarkup AdminKeyboard() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("🗑 Очистить историю оплат", TelegramCallbackData.Admin("clear-history")) },
        new[] { InlineKeyboardButton.WithCallbackData("⚠️ Удалить платежи и историю", TelegramCallbackData.Admin("delete-payments")) }
    });

    public static ReplyKeyboardMarkup? ConversationKeyboard(string response) =>
        IsCompleted(response) ? MainMenuKeyboard() : AddStepKeyboard(response);

    public static ReplyKeyboardMarkup? ConversationKeyboard(string response, bool isAdmin) =>
        IsCompleted(response) ? MainMenuKeyboard(isAdmin) : AddStepKeyboard(response);

    public static string FormatPaymentCard(PaymentListItem payment, DateOnly today) =>
        $"{PaymentDisplayNames.PaymentMethodIcon(payment.PaymentMethod)}{(payment.IsAutoDebit ? " 🤖" : string.Empty)} {payment.Name}\n\n" +
        $"{TelegramPresentation.Money(payment.Amount, payment.Currency)} · " +
        $"{TelegramPresentation.Schedule(payment.RecurrenceInterval, payment.RecurrenceUnit, payment.DueDate, payment.ScheduleDayOfMonth, payment.IsLastDayOfMonth)}\n" +
        $"Следующий платеж: {TelegramPresentation.Date(payment.DueDate, today)}";

    public static string FormatUpcomingCard(UpcomingPaymentItem payment, DateOnly today) =>
        $"{(payment.IsOverdue ? "⚠️" : PaymentDisplayNames.PaymentMethodIcon(payment.PaymentMethod))} {payment.Name}\n\n" +
        $"{TelegramPresentation.Money(payment.Amount, payment.Currency)} · " +
        $"{TelegramPresentation.Schedule(payment.RecurrenceInterval, payment.RecurrenceUnit, payment.DueDate, payment.ScheduleDayOfMonth, payment.IsLastDayOfMonth)}\n" +
        (payment.IsOverdue
            ? $"Срок был {TelegramPresentation.Date(payment.DueDate, today)}"
            : payment.DaysFromToday switch
            {
                0 => "сегодня",
                1 => "завтра",
                _ => $"{TelegramPresentation.Date(payment.DueDate, today)} · {TelegramPresentation.RelativeDays(payment.DaysFromToday)}"
            });

    public static InlineKeyboardMarkup PaymentActionKeyboard(IReadOnlyList<PaymentListItem> payments, string action) =>
        new(payments.Select(payment => new[]
        {
            InlineKeyboardButton.WithCallbackData($"{ActionLabel(action)}: {payment.Name} — {TelegramPresentation.Money(payment.Amount, payment.Currency)}",
                action == "pay"
                    ? TelegramCallbackData.Period("p", payment.Id, payment.DueDate) : TelegramCallbackData.Payment(action, payment.Id))
        }));

    public static InlineKeyboardMarkup PaymentCardKeyboard(Guid paymentId, DateOnly? dueDate)
    {
        var edit = InlineKeyboardButton.WithCallbackData("✏️ Изменить", TelegramCallbackData.Payment("edit", paymentId));
        return new InlineKeyboardMarkup(new[]
        {
            dueDate is { } date
                ? new[] { InlineKeyboardButton.WithCallbackData("✅ Оплатил", TelegramCallbackData.Period("p", paymentId, date)), edit }
                : new[] { edit }
        });
    }

    public static InlineKeyboardMarkup EditFieldsKeyboard(Guid paymentId) => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("💰 Сумма", TelegramCallbackData.EditField("amount", paymentId)), InlineKeyboardButton.WithCallbackData("📅 Расписание", TelegramCallbackData.EditField("schedule", paymentId)) },
        new[] { InlineKeyboardButton.WithCallbackData("💳 Способ", TelegramCallbackData.EditField("method", paymentId)), InlineKeyboardButton.WithCallbackData("🔁 Автосписание", TelegramCallbackData.EditField("autopay", paymentId)) },
        new[] { InlineKeyboardButton.WithCallbackData("✏️ Название", TelegramCallbackData.EditField("name", paymentId)), InlineKeyboardButton.WithCallbackData("📝 Комментарий", TelegramCallbackData.EditField("comment", paymentId)) },
        new[] { InlineKeyboardButton.WithCallbackData("🔔 Напоминания", TelegramCallbackData.ReminderSettings("payment", paymentId)) },
        new[] { InlineKeyboardButton.WithCallbackData("⏸ Отключить платеж", TelegramCallbackData.Payment("disable", paymentId)) },
        new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", TelegramCallbackData.Payment("back", paymentId)) }
    });

    public static InlineKeyboardMarkup MonthNavigationKeyboard(string prefix, int year, int month)
    {
        var current = new DateOnly(year, month, 1);
        var previous = current.AddMonths(-1);
        var next = current.AddMonths(1);
        return new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData($"◀️ {previous.ToString("MMMM", CultureInfo.GetCultureInfo("ru-RU"))}", TelegramCallbackData.Month(prefix, previous.Year, previous.Month)),
                InlineKeyboardButton.WithCallbackData($"{next.ToString("MMMM", CultureInfo.GetCultureInfo("ru-RU"))} ▶️", TelegramCallbackData.Month(prefix, next.Year, next.Month))
            }
        });
    }

    public static string FormatHistory(int year, int month, IReadOnlyList<PaymentTransactionItem> history)
    {
        var title = new DateOnly(year, month, 1).ToString("MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU"));
        return history.Count == 0
            ? $"📜 История · {title}\n\nЗа выбранный месяц оплат нет."
            : $"📜 История · {title}\n\n{FormatHistoryItems(history)}";
    }

    private static string FormatHistoryItems(IReadOnlyList<PaymentTransactionItem> history) =>
        string.Join("\n\n", history.Select(x =>
            $"✅ {x.PaymentName} — {TelegramPresentation.Money(x.PaidAmount, x.Currency)}\n" +
            $"Оплачено {TelegramPresentation.Date(x.PaidDate, DateOnly.MinValue)}\n" +
            $"Платеж за {PaymentMonth(x.PaidPeriod)}"));

    public static string FormatStatistics(int year, int month, IReadOnlyList<MonthlyStatisticsCurrency> statistics)
    {
        var title = $"📊 {Russian.TextInfo.ToTitleCase(new DateOnly(year, month, 1).ToString("MMMM yyyy", Russian))}";
        if (statistics.Count == 0)
            return $"{title}\n\nПлатежей и оплат нет.";

        var blocks = statistics.Select(x =>
            $"{FormatStatisticsAmounts(x.Currency, x.PlannedAmount, x.PaidAmount, x.RemainingAmount)}\n" +
            (x.UnpaidCount == 0 ? "Все платежи оплачены" : $"Не оплачено платежей: {x.UnpaidCount}"));
        return $"{title}\n\n{string.Join("\n\n", blocks)}";
    }

    public static string FormatStatistics(int year, int month, IReadOnlyList<MonthlyStatisticsCurrency> monthly,
        IReadOnlyList<AnnualStatisticsCurrency> annual)
    {
        var monthlyText = FormatStatistics(year, month, monthly);
        if (annual.Count == 0)
            return monthlyText;

        var annualBlocks = annual.Select(x =>
            $"{FormatStatisticsAmounts(x.Currency, x.PlannedAmount, x.PaidAmount, x.RemainingAmount)}\n" +
            $"Платежей: оплачено {x.PaidCount} из {x.PlannedCount}");
        return $"{monthlyText}\n\n📅 {year} год\n\n{string.Join("\n\n", annualBlocks)}";
    }

    private static string FormatStatisticsAmounts(string currency, decimal planned, decimal paid, decimal remaining) =>
        $"Запланировано: {TelegramPresentation.Money(planned, currency)}\n" +
        $"Оплачено: {TelegramPresentation.Money(paid, currency)}\n" +
        $"Осталось: {TelegramPresentation.Money(remaining, currency)}";

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
        "🛠 Админская панель" or "Админская панель" => "/admin",
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
        var timeZone = TimeZoneResolver.Resolve(timeZoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone));
    }

    private static ReplyKeyboardMarkup? AddStepKeyboard(string response)
    {
        if (response.Contains("Сохранить часовой пояс", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Да", "Нет" }, new KeyboardButton[] { "Отмена" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("Введите текущее местное время", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Отмена" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("Изменить дату", StringComparison.OrdinalIgnoreCase)
            || response.Contains("Добавить комментарий", StringComparison.OrdinalIgnoreCase))
        {
            return new ReplyKeyboardMarkup(new[]
            {
                new KeyboardButton[] { "✅ Сохранить", "📅 Изменить дату" },
                new KeyboardButton[] { "📝 Добавить комментарий", "Отмена" }
            })
            { ResizeKeyboard = true, OneTimeKeyboard = true };
        }
        if (response.Contains("Сохранить", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Да", "Нет" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("Ожидаемая сумма", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Ожидаемая сумма" }, new KeyboardButton[] { "Отмена" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("Без комментария", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Без комментария" }, new KeyboardButton[] { "Отмена" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("день месяца", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[]
            {
                new KeyboardButton[] { "1-е число", "15-е число" },
                new KeyboardButton[] { "Последний день месяца" },
                new KeyboardButton[] { "Произвольная дата" },
                new KeyboardButton[] { "Отмена" }
            })
            { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("Укажите дату платежа", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Отмена" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("дату", StringComparison.OrdinalIgnoreCase) || response.Contains("Сегодня", StringComparison.OrdinalIgnoreCase))
        {
            var rows = new List<KeyboardButton[]>
            {
                new KeyboardButton[] { "Сегодня", "Завтра" },
                new KeyboardButton[] { "Через неделю" },
                new KeyboardButton[] { "Первое число следующего месяца" },
                new KeyboardButton[] { "То же число следующего месяца" }
            };
            if (response.Contains("Оставить текущее", StringComparison.OrdinalIgnoreCase))
                rows.Add(new KeyboardButton[] { "Оставить текущее" });
            rows.Add(new KeyboardButton[] { "Отмена" });
            return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true, OneTimeKeyboard = true };
        }
        if (response.Contains("периодичность", StringComparison.OrdinalIgnoreCase) || response.Contains("как часто", StringComparison.OrdinalIgnoreCase))
        {
            var rows = new List<KeyboardButton[]>
            {
                new KeyboardButton[] { "Каждую неделю", "Каждый месяц" },
                new KeyboardButton[] { "Каждый год", "Один раз" }
            };
            if (response.Contains("Оставить текущее", StringComparison.OrdinalIgnoreCase))
                rows.Add(new KeyboardButton[] { "Оставить текущее" });
            return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true, OneTimeKeyboard = true };
        }
        if (response.Contains("способ оплаты", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Карта", "Банковский перевод" }, new KeyboardButton[] { "Наличные", "Другое" }, new KeyboardButton[] { "Оставить текущий" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("автосписание", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Да", "Нет" }, new KeyboardButton[] { "Оставить текущее" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("день недели", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[]
            {
                new KeyboardButton[] { "Понедельник", "Вторник" },
                new KeyboardButton[] { "Среда", "Четверг" },
                new KeyboardButton[] { "Пятница", "Суббота" },
                new KeyboardButton[] { "Воскресенье" },
                new KeyboardButton[] { "Отмена" }
            })
            { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("Оставить текущее", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Оставить текущее" }, new KeyboardButton[] { "Отмена" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("отмена", StringComparison.OrdinalIgnoreCase))
            return new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Отмена" } }) { ResizeKeyboard = true, OneTimeKeyboard = true };
        if (response.Contains("Введите", StringComparison.OrdinalIgnoreCase))
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
