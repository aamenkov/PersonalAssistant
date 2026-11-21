using PersonalAssistant.Application;
using PersonalAssistant.Bot;
using PersonalAssistant.Domain;

namespace PersonalAssistant.UnitTests;

public sealed class PresentationRulesTests
{
    [Theory]
    [InlineData(1, RecurrenceUnit.Month, "каждый месяц")]
    [InlineData(3, RecurrenceUnit.Month, "каждые 3 месяца")]
    [InlineData(1, RecurrenceUnit.Year, "каждый год")]
    [InlineData(1, RecurrenceUnit.Once, "разовый платеж")]
    public void Recurrence_IsHumanReadable(int interval, RecurrenceUnit unit, string expected)
    {
        Assert.Equal(expected, PaymentDisplayNames.Recurrence(interval, unit));
    }

    [Fact]
    public void DateShortcut_SupportsOneWeek()
    {
        Assert.True(DateShortcutCalculator.TryParse("Через неделю", new DateOnly(2026, 8, 15), out var result));
        Assert.Equal(new DateOnly(2026, 8, 22), result);
    }

    [Theory]
    [InlineData(2800, "2 800,00 ₽")]
    [InlineData(30.5, "30,50 ₽")]
    public void Money_UsesRussianTwoDecimalFormat(decimal amount, string expected)
    {
        Assert.Equal(expected, TelegramPresentation.Money(amount, "RUB"));
    }

    [Fact]
    public void SettingsKeyboard_UsesActionLabelsWithoutDuplicatedValues()
    {
        var rows = TelegramUi.SettingsKeyboard().InlineKeyboard.ToArray();

        Assert.Equal(2, rows.Length);
        Assert.Equal([("Часовой пояс", "settings:timezone"), ("Валюта", "settings:currency:menu")],
            rows[0].Select(button => (button.Text, button.CallbackData)));
        Assert.Equal([("Напоминания", "settings:days:menu"), ("Время", "settings:time:menu")],
            rows[1].Select(button => (button.Text, button.CallbackData)));
    }

    [Fact]
    public void Settings_ShowCurrentValuesAndOfferReturnFromEverySelector()
    {
        var user = new User(42, 42, null, null, DateTime.UnixEpoch);
        user.SetTimeZone("Europe/Moscow", DateTime.UnixEpoch);
        user.SetDefaultCurrency("EUR", DateTime.UnixEpoch);
        user.SetReminderSettings(new TimeOnly(12, 30), 7, DateTime.UnixEpoch);

        Assert.Equal("⚙️ Настройки\n\nЧасовой пояс: Москва (UTC+3)\nВалюта: EUR\n" +
            "Напоминать: за 7 дней\nВремя напоминаний: 12:30", TelegramUi.FormatSettings(user));

        var timeZones = TelegramUi.TimeZoneKeyboard(fromSettings: true);
        Assert.Contains(timeZones.InlineKeyboard.SelectMany(row => row), button => button.CallbackData == "timezone:settings:Europe/Moscow");
        Assert.Contains(TelegramUi.TimeZoneKeyboard().InlineKeyboard.SelectMany(row => row), button => button.CallbackData == "timezone:Europe/Moscow");

        foreach (var keyboard in new[] { timeZones, TelegramUi.CurrencyKeyboard(), TelegramUi.ReminderDaysKeyboard(), TelegramUi.ReminderTimeKeyboard() })
            Assert.Equal("settings:back", Assert.Single(keyboard.InlineKeyboard.Last()).CallbackData);
    }

    [Fact]
    public void Dates_AreRelativeNearbyAndIncludeYearAcrossYearBoundary()
    {
        var today = new DateOnly(2026, 11, 27);

        Assert.Equal("сегодня", TelegramPresentation.Date(today, today));
        Assert.Equal("завтра", TelegramPresentation.Date(today.AddDays(1), today));
        Assert.Equal("30 ноября", TelegramPresentation.Date(today.AddDays(3), today));
        Assert.Equal("через 3 дня", TelegramPresentation.RelativeDays(3));
        Assert.Equal("11 января 2027", TelegramPresentation.Date(new DateOnly(2027, 1, 11), today));
    }

    [Theory]
    [InlineData(false, "каждый месяц, 31 числа")]
    [InlineData(true, "каждый месяц, в последний день месяца")]
    public void PaymentCard_DistinguishesScheduleFromShortMonthDueDate(bool lastDay, string schedule)
    {
        var dueDate = new DateOnly(2027, 2, 28);
        var payment = new PaymentListItem(Guid.NewGuid(), "Интернет", 2800, "RUB", dueDate, 1,
            RecurrenceUnit.Month, PaymentMethod.Card, false, 31, lastDay);

        var card = TelegramUi.FormatPaymentCard(payment, new DateOnly(2027, 2, 25));

        Assert.Contains($"2 800,00 ₽ · {schedule}", card);
        Assert.Contains("Следующий платеж: 28 февраля", card);
    }

    [Fact]
    public void PaymentCardKeyboard_KeepsActionsBoundToPaymentId()
    {
        var paymentId = Guid.NewGuid();
        var keyboard = TelegramUi.PaymentCardKeyboard(paymentId);
        var callbacks = keyboard.InlineKeyboard.SelectMany(row => row).Select(button => button.CallbackData).ToArray();

        Assert.Contains($"payment:pay:{paymentId}", callbacks);
        Assert.Contains($"payment:edit:{paymentId}", callbacks);
    }
}
