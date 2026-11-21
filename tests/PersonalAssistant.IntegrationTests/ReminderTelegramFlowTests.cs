using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalAssistant.Application;
using PersonalAssistant.Bot;
using PersonalAssistant.Domain;
using PersonalAssistant.Infrastructure;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit.Abstractions;
using User = PersonalAssistant.Domain.User;

namespace PersonalAssistant.IntegrationTests;

public sealed class ReminderTelegramFlowTests(ITestOutputHelper output)
{
    private const long TelegramId = 100;
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly DateOnly Due = new(2026, 12, 20);

    [ReminderProfilePostgresFact]
    public async Task TelegramScreens_RenderSavedProfilesDraftAndSnoozeFromActualHandlers()
    {
        // Capture the actual Telegram method payloads and check that each supported screen is reachable.
        await using var flow = new Flow(ReminderProfileMigrationTests.CreateOptions(), new DateTimeOffset(2026, 12, 16, 9, 0, 0, TimeSpan.Zero));
        var (user, _) = await flow.SeedAsync(ReminderMode.Ordinary, anotherPayment: true);
        await flow.CheckAsync(async db =>
        {
            Assert.True(await new UserRepository(db).TrySetReminderProfileAsync(TelegramId, user.GetReminderProfile(),
                ReminderProfile.DefaultFor(ReminderMode.Ordinary), flow.Clock.Now.UtcDateTime, Token));
        });

        void Capture(string title, string expected, string key)
        {
            Assert.Contains(expected, flow.Current.Text);
            Assert.Contains(key, flow.Current.Buttons.Keys);
            output.WriteLine($"--- {title} ---\n{flow.Current.Text}\n{string.Join('\n', flow.Current.ButtonRows)}");
        }

        await flow.TypeAsync("/settings");
        await flow.TapAsync("🔔 Напоминания");
        Capture("Обычный", "Режим: Обычный", "Изменить");
        await flow.TapAsync("Изменить");
        await flow.TapAsync("Изменить режим");
        await flow.TapAsync("Настойчивый");
        await flow.TapAsync("Сохранить");
        Capture("Настойчивый", "Режим: Настойчивый", "Другой пример");
        await flow.TapAsync("Изменить");
        await flow.TapAsync("Изменить режим");
        await flow.TapAsync("Критический");
        await flow.TapAsync("Сохранить");
        Capture("Критический", "09:00–21:00, каждый час (13)", "◀️ Назад");
        await flow.TapAsync("Изменить");
        await flow.TapAsync("Интервал");
        await flow.TapAsync("2 часа");
        Capture("Предпросмотр черновика", "09:00–21:00, каждые 2 часа (7)", "Сохранить");
        await flow.TapAsync("Отмена");

        await flow.OpenPaymentReminderSettingsAsync();
        await flow.TapAsync("Настроить отдельно");
        await flow.TapAsync("Сохранить");
        Capture("Индивидуальный профиль", "Индивидуальные настройки.", "Вернуть общие настройки");
        flow.Clock.Now = new DateTimeOffset(2026, 12, 20, 9, 0, 0, TimeSpan.Zero);
        await flow.TapAsync("Отсрочка");
        Capture("Установка отсрочки", "Срок платежа: 20.12.2026", "Сегодня до 19:00");
        await flow.TapAsync("Сегодня до 19:00");
        Capture("Отсрочка установлена", "Напоминания отложены до 20.12.2026 19:00", "Открыть отсрочку");
        await flow.TapAsync("Открыть отсрочку");
        Capture("Действующая отсрочка", "Отложено до 20.12.2026 19:00", "Отменить отсрочку");
        await flow.TapAsync("Отменить отсрочку");
        Capture("Отмена отсрочки", "Отсрочка отменена.", "◀️ К платежу");
    }

    [ReminderProfilePostgresFact]
    public async Task SharedAndIndividualProfiles_PreviewBeforeSaveAndDoNotOverwriteNewerSettings()
    {
        // Drive real Telegram routing against PostgreSQL, including discard, compare-and-save, and inheritance.
        await using var flow = new Flow(ReminderProfileMigrationTests.CreateOptions(), new DateTimeOffset(2026, 12, 17, 9, 0, 0, TimeSpan.Zero));
        var (user, payment) = await flow.SeedAsync(ReminderMode.Ordinary, anotherPayment: true);
        await flow.TypeAsync("/settings");
        await flow.TapAsync("🔔 Напоминания");
        Assert.Contains("Пример общего профиля: Интернет", flow.Current.Text);
        await flow.CheckAsync(async db => Assert.False(await db.ConversationStates.AnyAsync()));
        await flow.TapAsync("Другой пример");
        await flow.TapAsync("Телефон");
        Assert.Contains("Пример общего профиля: Телефон", flow.Current.Text);
        await flow.TapAsync("Изменить");
        await flow.TapAsync("Другой пример");
        await flow.TapAsync("Интернет");
        var original = user.GetReminderProfile();
        await flow.TapAsync("Изменить режим");
        await flow.TapAsync("Критический");
        await flow.TapAsync("Дни до оплаты");
        await flow.TapAsync("Ввести своё");
        await flow.TypeAsync("31");
        Assert.Contains("Не получилось", flow.Current.Text);
        await flow.TypeAsync("5");
        await flow.TapAsync("Время");
        await flow.TapAsync("Ввести своё");
        await flow.TypeAsync("25:10");
        Assert.Contains("Не получилось", flow.Current.Text);
        await flow.TypeAsync("08:45");
        await flow.TapAsync("Интервал");
        await flow.TapAsync("2 часа");
        await flow.TapAsync("Начало окна");
        await flow.TapAsync("Ввести своё");
        await flow.TypeAsync("22:00");
        Assert.Contains("начало должно быть раньше", flow.Current.Text);
        await flow.TypeAsync("10:00");
        await flow.TapAsync("Конец окна");
        await flow.TapAsync("18:00");
        Assert.Contains("10:00–18:00, каждые 2 часа (5)", flow.Current.Text);
        Assert.Contains("08:45", flow.Current.Text);
        Assert.Contains("UTC+3", flow.Current.Text);
        await flow.TapAsync("Изменить режим");
        await flow.TapAsync("Критический");
        Assert.Contains("10:00–18:00", flow.Current.Text);
        await flow.CheckAsync(async db =>
        {
            Assert.Equal(original, (await db.Users.SingleAsync()).GetReminderProfile());
            Assert.Empty(await db.Reminders.ToListAsync());
        });
        await flow.TapAsync("Отмена");
        Assert.Contains("Режим: Обычный", flow.Current.Text);

        await flow.TapAsync("Изменить");
        await flow.TapAsync("Изменить режим");
        await flow.TapAsync("Критический");
        var oldSave = flow.Current.Callback("Сохранить");
        var screen = flow.Current.Id;
        flow.FailNextEdit = true;
        await flow.TapAsync("Сохранить");
        Assert.NotEqual(screen, flow.Current.Id);
        Assert.Contains("Режим: Критический", flow.Current.Text);
        await flow.PressAsync(oldSave, screen);
        Assert.Contains("не актуален", flow.Current.Text);

        await flow.OpenPaymentReminderSettingsAsync();
        Assert.Contains("Используются общие", flow.Current.Text);
        await flow.TapAsync("Настроить отдельно");
        await flow.TapAsync("Изменить режим");
        await flow.TapAsync("Настойчивый");
        await flow.TapAsync("Сохранить");
        Assert.Contains("Индивидуальные настройки", flow.Current.Text);
        await flow.TypeAsync("/settings");
        await flow.TapAsync("🔔 Напоминания");
        await flow.TapAsync("Изменить");
        await flow.TapAsync("Дни до оплаты");
        await flow.TapAsync("7 дней");
        await flow.TapAsync("Сохранить");
        Assert.Contains("за 7 дней", flow.Current.Text);
        await flow.CheckAsync(async db =>
        {
            var effective = await new PaymentReminderSettingsService(new PaymentRepository(db)).GetEffectiveAsync(user.Id, payment.Id, Token);
            Assert.Equal(new EffectiveReminderProfile(ReminderProfile.DefaultFor(ReminderMode.Persistent), false), effective);
        });

        await flow.TapAsync("Изменить");
        await flow.TapAsync("Дни до оплаты");
        await flow.TapAsync("1 день");
        var concurrent = new ReminderProfile(ReminderMode.Persistent, 14, new(10, 15), OverdueMode.Disabled);
        await flow.CheckAsync(async db =>
        {
            var current = (await db.Users.SingleAsync()).GetReminderProfile();
            Assert.True(await new UserRepository(db).TrySetReminderProfileAsync(TelegramId, current, concurrent, flow.Clock.Now.UtcDateTime, Token));
        });
        await flow.TapAsync("Сохранить");
        Assert.Contains("успели измениться", flow.Current.Text);
        await flow.CheckAsync(async db => Assert.Equal(concurrent, (await db.Users.SingleAsync()).GetReminderProfile()));

        await flow.OpenPaymentReminderSettingsAsync();
        await flow.TapAsync("Вернуть общие настройки");
        Assert.Contains("будут удалены", flow.Current.Text);
        await flow.TapAsync("Подтвердить");
        Assert.Contains("Используются общие", flow.Current.Text);
        await flow.TapAsync("Настроить отдельно");
        await flow.TapAsync("Сохранить");
        await flow.TapAsync("Отключить уведомления");
        Assert.Contains("Индивидуальные настройки будут удалены", flow.Current.Text);
        await flow.TapAsync("Подтвердить");
        Assert.Contains("Уведомления выключены", flow.Current.Text);
        await flow.TapAsync("Включить с общими настройками");
        Assert.Contains("Используются общие", flow.Current.Text);
        await flow.CheckAsync(async db =>
        {
            Assert.False(await db.Set<PaymentReminderSettings>().AnyAsync());
            Assert.False(await db.ConversationStates.AnyAsync());
            Assert.False(await db.Reminders.AnyAsync());
        });

        await flow.TypeAsync("/settings");
        await flow.TapAsync("🔔 Напоминания");
        await flow.TapAsync("Изменить");
        flow.Clock.Now = flow.Clock.Now.AddMinutes(31);
        await flow.TapAsync("Сохранить");
        Assert.Contains("уже не актуален", flow.Current.Text);
        await flow.CheckAsync(async db =>
        {
            Assert.False(await db.ConversationStates.AnyAsync());
            Assert.Equal(concurrent, (await db.Users.SingleAsync()).GetReminderProfile());
        });
    }

    [ReminderProfilePostgresFact]
    public async Task ReminderActions_ChangeAndCancelSnoozeAndNeverActOnTheNextPeriod()
    {
        // Exercise real worker callbacks and payment routing on a moving clock instead of sending to Telegram.
        await using var flow = new Flow(ReminderProfileMigrationTests.CreateOptions(), new DateTimeOffset(2026, 12, 20, 9, 0, 0, TimeSpan.Zero));
        var (_, payment) = await flow.SeedAsync(ReminderMode.Critical);
        await flow.PassAsync();
        var reminder = flow.Current;
        var pay = reminder.Callback("✅ Оплатил");
        var snooze = reminder.Callback("⏰ Напомнить позже");
        Assert.Equal(TelegramCallbackData.Period("p", payment.Id, Due), pay);
        Assert.Equal(TelegramCallbackData.Period("s", payment.Id, Due), snooze);

        await flow.TapAsync("⏰ Напомнить позже");
        await flow.TapAsync("Через 1 час");
        Assert.Contains("13:00", flow.Current.Text);
        await flow.TapAsync("Открыть отсрочку");
        Assert.Contains("Отложено до", flow.Current.Text);
        await flow.TapAsync("Через 3 часа");
        Assert.Contains("15:00", flow.Current.Text);
        await flow.TapAsync("Открыть отсрочку");
        await flow.TapAsync("Завтра в 09:00");
        Assert.Contains("21.12.2026 09:00", flow.Current.Text);
        await flow.TapAsync("Открыть отсрочку");
        await flow.TapAsync("Указать время");
        await flow.TypeAsync("20.12.2026 08:00");
        Assert.Contains("в будущем", flow.Current.Text);
        await flow.TypeAsync("20.12.2026 22:00");
        Assert.Contains("22:00", flow.Current.Text);
        await flow.TapAsync("Открыть отсрочку");
        await flow.TapAsync("Отменить отсрочку");
        Assert.Contains("Отсрочка отменена", flow.Current.Text);
        var sent = flow.Sends;
        await flow.PassAsync();
        Assert.Equal(sent, flow.Sends);

        await flow.TapAsync("Открыть отсрочку");
        await flow.TapAsync("Сегодня до 19:00");
        Assert.Contains("19:00", flow.Current.Text);
        flow.Clock.Now = flow.Clock.Now.AddHours(2);
        await flow.PassAsync();
        Assert.Equal(sent, flow.Sends);
        flow.Clock.Now = flow.Clock.Now.AddHours(5);
        await flow.PassAsync();
        Assert.Equal(sent + 1, flow.Sends);
        Assert.Contains("Пора проверить", flow.Current.Text);
        await flow.PassAsync();
        Assert.Equal(sent + 1, flow.Sends);
        var afterSnooze = flow.Current;
        await flow.TapAsync("✅ Оплатил");
        await flow.TypeAsync("Ожидаемая сумма");
        await flow.TypeAsync("Сохранить");
        Assert.Contains("Оплата сохранена", flow.Current.Text);

        await flow.PressAsync(pay, reminder.Id);
        Assert.Contains("не актуален", flow.Current.Text);
        await flow.PressAsync(snooze, reminder.Id);
        Assert.Contains("не актуален", flow.Current.Text);
        await flow.PressAsync($"payment:pay:{payment.Id}", afterSnooze.Id);
        Assert.Contains("выберите действие заново", flow.Current.Text);
        await flow.PressAsync($"reminder:snooze:{payment.Id}:hour", afterSnooze.Id);
        Assert.Contains("выберите действие заново", flow.Current.Text);
        await flow.CheckAsync(async db =>
        {
            var recorded = await db.PaymentTransactions.SingleAsync();
            Assert.Equal("2026-12-20", recorded.PaidPeriod);
            Assert.Equal(Due, recorded.PaidDate);
            Assert.Equal(Due.AddMonths(1), (await db.RecurringPayments.SingleAsync()).NextPaymentDate);
            Assert.False(await db.ConversationStates.AnyAsync());
        });
        sent = flow.Sends;
        await flow.PassAsync();
        Assert.Equal(sent, flow.Sends);
    }

    [ReminderProfilePostgresFact]
    public async Task PeriodChangedAfterOpeningPayment_PreventsConfirmingAnotherPeriod()
    {
        // Verify the confirmation uses the period captured by the button, not the latest due date in the database.
        await using var flow = new Flow(ReminderProfileMigrationTests.CreateOptions(), new DateTimeOffset(2026, 12, 20, 9, 0, 0, TimeSpan.Zero));
        var (user, payment) = await flow.SeedAsync(ReminderMode.Ordinary);
        await flow.CheckAsync(async db =>
        {
            db.Add(ConversationState.Create(user.Id, ConversationKind.RecordPayment,
                JsonSerializer.Serialize(new { PaymentId = payment.Id, Step = 3, PaidAmount = 123, PaidDate = Due }),
                flow.Clock.Now.UtcDateTime));
            await db.SaveChangesAsync();
        });
        await flow.TypeAsync("Сохранить");
        Assert.Contains("откройте актуальный платеж заново", flow.Current.Text);
        await flow.TypeAsync("/pay");
        await flow.TapAsync(flow.Current.Buttons.Keys.Single());
        await flow.TypeAsync("Ожидаемая сумма");
        await flow.CheckAsync(db => db.RecurringPayments.Where(x => x.Id == payment.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.NextPaymentDate, Due.AddDays(1))));
        await flow.TypeAsync("Сохранить");
        Assert.Contains("срок больше не актуален", flow.Current.Text);
        await flow.CheckAsync(async db =>
        {
            Assert.False(await db.PaymentTransactions.AnyAsync());
            Assert.False(await db.ConversationStates.AnyAsync());
        });
    }

    [ReminderProfilePostgresFact]
    public async Task ConcurrentPayment_ClearsTheDraftWithoutTryingToSaveTheLosingTransactionAgain()
    {
        var options = ReminderProfileMigrationTests.CreateOptions();
        await using var flow = new Flow(options, new DateTimeOffset(2026, 12, 20, 9, 0, 0, TimeSpan.Zero));
        var (user, payment) = await flow.SeedAsync(ReminderMode.Ordinary);
        var intercept = new CompetingPaymentSave(async () =>
        {
            await using var winner = new PersonalAssistantDbContext(options);
            var result = await new PaymentService(new PaymentRepository(winner))
                .RecordPaymentAsync(user.Id, payment.Id, Due, 123, Due, null, Token);
            Assert.Equal(PaymentRecordStatus.Recorded, result.Status);
        });
        var losingOptions = new DbContextOptionsBuilder<PersonalAssistantDbContext>(options).AddInterceptors(intercept).Options;
        await using var loser = new PersonalAssistantDbContext(losingOptions);
        var conversation = new PaymentRecordConversationService(new ConversationStateRepository(loser), new PaymentService(new PaymentRepository(loser)));
        await conversation.BeginAsync(user.Id, payment.Id, Due, Due, Token);
        await conversation.HandleInputAsync(user.Id, "Ожидаемая сумма", Token);

        var reply = await conversation.HandleInputAsync(user.Id, "Сохранить", Token);
        Assert.Contains("Оплата уже сохранена", reply);
        await flow.CheckAsync(async db =>
        {
            Assert.Equal("2026-12-20", (await db.PaymentTransactions.SingleAsync()).PaidPeriod);
            Assert.False(await db.ConversationStates.AnyAsync());
            Assert.Equal(Due.AddMonths(1), (await db.RecurringPayments.SingleAsync()).NextPaymentDate);
        });
    }

    private sealed class CompetingPaymentSave(Func<Task> commitOtherPayment) : SaveChangesInterceptor
    {
        private bool committed;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!committed && eventData.Context!.ChangeTracker.Entries<PaymentTransaction>().Any(x => x.State == EntityState.Added))
            {
                committed = true;
                await commitOtherPayment();
            }
            return result;
        }
    }

    [ReminderProfilePostgresFact]
    public async Task SnoozeAndProfileWrites_OnlyOneCompetingActionWins()
    {
        // Check PostgreSQL arbitration, not just sequential callbacks in the same bot process.
        var options = ReminderProfileMigrationTests.CreateOptions();
        await using var flow = new Flow(options, new DateTimeOffset(2026, 12, 20, 9, 0, 0, TimeSpan.Zero));
        var (user, payment) = await flow.SeedAsync(ReminderMode.Ordinary);
        await using var first = new PersonalAssistantDbContext(options);
        await using var second = new PersonalAssistantDbContext(options);
        var firstReminders = new ReminderRepository(first, flow.Clock);
        var secondReminders = new ReminderRepository(second, flow.Clock);
        var now = flow.Clock.Now.UtcDateTime;
        Assert.True(await firstReminders.UpsertSnoozeAsync(user.Id, payment.Id, Due, null, now.AddHours(1), Token));
        var snooze = (await first.ReminderSnoozes.AsNoTracking().SingleAsync()).Id;
        var results = await Task.WhenAll(
            firstReminders.CancelSnoozeAsync(user.Id, payment.Id, Due, snooze, Token),
            secondReminders.UpsertSnoozeAsync(user.Id, payment.Id, Due, snooze, now.AddHours(3), Token));
        Assert.Single(results, result => result);
        var current = await first.ReminderSnoozes.AsNoTracking().SingleAsync();
        if (results[1])
        {
            Assert.NotEqual(snooze, current.Id);
            Assert.Null(current.ConsumedAtUtc);
            Assert.False(await firstReminders.CancelSnoozeAsync(user.Id, payment.Id, Due, snooze, Token));
        }
        else
            Assert.Equal(now, current.ConsumedAtUtc);
        Assert.False(await firstReminders.CancelSnoozeAsync(Guid.NewGuid(), payment.Id, Due, current.Id, Token));
        Assert.False(await firstReminders.UpsertSnoozeAsync(user.Id, payment.Id, Due.AddMonths(1), null, now.AddHours(1), Token));
        Assert.False(await firstReminders.UpsertSnoozeAsync(user.Id, payment.Id, Due,
            current.ConsumedAtUtc is null ? current.Id : null, now.AddSeconds(-1), Token));

        var inherited = new EffectiveReminderProfile(user.GetReminderProfile(), true);
        var overrides = await Task.WhenAll(
            new PaymentRepository(first).TryChangeReminderProfileAsync(user.Id, payment.Id, inherited,
                new(ReminderProfile.DefaultFor(ReminderMode.Critical), false), Token),
            new PaymentRepository(second).TryChangeReminderProfileAsync(user.Id, payment.Id, inherited,
                new(ReminderProfile.DefaultFor(ReminderMode.Persistent), false), Token));
        Assert.Single(overrides, result => result);
        Assert.Single(await first.Set<PaymentReminderSettings>().AsNoTracking().ToListAsync());
    }

    [ReminderProfilePostgresFact]
    public async Task SnoozeMenu_DoesNotShowANewerSnoozeUnderAnOldCancelButton()
    {
        // Resuming an old custom-input screen must not retarget its cancel button after a concurrent change.
        await using var flow = new Flow(ReminderProfileMigrationTests.CreateOptions(), new DateTimeOffset(2026, 12, 20, 9, 0, 0, TimeSpan.Zero));
        var (user, payment) = await flow.SeedAsync(ReminderMode.Ordinary);
        await flow.OpenPaymentReminderSettingsAsync();
        await flow.TapAsync("Отсрочка");
        await flow.TapAsync("Через 1 час");
        await flow.TapAsync("Открыть отсрочку");
        var oldRemove = flow.Current.Callback("Отменить отсрочку");
        await flow.TapAsync("Указать время");
        await flow.CheckAsync(async db =>
        {
            var repository = new ReminderRepository(db, flow.Clock);
            var current = await db.ReminderSnoozes.SingleAsync();
            var now = flow.Clock.Now.UtcDateTime;
            Assert.True(await repository.UpsertSnoozeAsync(user.Id, payment.Id, Due, current.Id, now.AddHours(4), Token));
        });
        await flow.TapAsync("◀️ Назад");
        Assert.Contains("Отсрочка уже изменилась", flow.Current.Text);
        await flow.PressAsync(oldRemove, flow.Current.Id);
        Assert.Contains("не актуален", flow.Current.Text);
        await flow.CheckAsync(async db => Assert.Null((await db.ReminderSnoozes.SingleAsync()).ConsumedAtUtc));
    }

    internal static async Task CheckMigratedUiAsync(DbContextOptions<PersonalAssistantDbContext> options, Guid paymentId, DateTimeOffset now)
    {
        // The Telegram editor uses the preserved settings of an upgraded account, not a clean-install preset.
        await using var flow = new Flow(options, now);
        await flow.TypeAsync("/settings");
        await flow.TapAsync("🔔 Напоминания");
        Assert.Contains("07:30", flow.Current.Text);
        await flow.TapAsync("Изменить");
        await flow.TapAsync("Время");
        await flow.TapAsync("Ввести своё");
        await flow.TypeAsync("07:45");
        await flow.TapAsync("Сохранить");
        Assert.Contains("07:45", flow.Current.Text);
        await flow.PressAsync(TelegramCallbackData.ReminderSettings("payment", paymentId), flow.Current.Id);
        await flow.TapAsync("Настроить отдельно");
        await flow.TapAsync("Сохранить");
        Assert.Contains("Индивидуальные настройки", flow.Current.Text);
        await flow.TapAsync("Вернуть общие настройки");
        await flow.TapAsync("Подтвердить");
        Assert.Contains("Используются общие", flow.Current.Text);
        await flow.CheckAsync(async db =>
        {
            Assert.Equal(new TimeOnly(7, 45), (await db.Users.SingleAsync(x => x.TelegramUserId == TelegramId)).ReminderTimeLocal);
            Assert.False(await db.Set<PaymentReminderSettings>().AnyAsync());
        });
    }

    private sealed class Flow : IAsyncDisposable
    {
        private readonly DbContextOptions<PersonalAssistantDbContext> options;
        private readonly ServiceProvider services;
        private readonly FakeTelegram transport = new();
        private readonly HttpClient http;
        private readonly ITelegramBotClient client;
        private readonly TelegramPollingService polling;
        private readonly Telegram.Bot.Types.User sender = new() { Id = TelegramId, FirstName = "Tester" };
        private int updateId = 1;
        internal ManualClock Clock { get; }
        internal MessageView Current => transport.Last!;
        internal int Sends => transport.Sends;
        internal bool FailNextEdit { set => transport.FailNextEdit = value; }

        internal Flow(DbContextOptions<PersonalAssistantDbContext> options, DateTimeOffset now)
        {
            this.options = options;
            Clock = new(now);
            http = new HttpClient(transport);
            client = new TelegramBotClient(new TelegramBotClientOptions("123456:test_only") { RetryCount = 0 }, http);
            var access = BotAccessPolicy.Parse("100");
            services = new ServiceCollection()
                .AddScoped(_ => new PersonalAssistantDbContext(options))
                .AddSingleton<TimeProvider>(Clock)
                .AddScoped<IUserRepository, UserRepository>()
                .AddScoped<UserSettingsService>()
                .AddScoped<IPaymentRepository, PaymentRepository>()
                .AddScoped<IPaymentReminderSettingsRepository, PaymentRepository>()
                .AddScoped<PaymentReminderSettingsService>()
                .AddScoped<PaymentService>()
                .AddScoped<IConversationStateRepository, ConversationStateRepository>()
                .AddScoped<TimeZoneConversationService>()
                .AddScoped<PaymentConversationService>()
                .AddScoped<PaymentRecordConversationService>()
                .AddScoped<PaymentEditConversationService>()
                .AddScoped<IReminderRepository, ReminderRepository>()
                .AddScoped<ReminderSnoozeService>()
                .AddScoped<ReminderService>()
                .AddScoped<ReminderTelegramHandler>()
                .AddScoped<ITelegramUpdateStore, TelegramUpdateStore>()
                .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            polling = new TelegramPollingService(client, services.GetRequiredService<IServiceScopeFactory>(),
                new UserUpdateGate(), access, BotAdminPolicy.Parse(null, access), NullLogger<TelegramPollingService>.Instance);
        }

        internal async Task<(User User, RecurringPayment Payment)> SeedAsync(ReminderMode mode, bool anotherPayment = false)
        {
            await using var db = new PersonalAssistantDbContext(options);
            await db.Database.MigrateAsync();
            var now = Clock.Now.UtcDateTime;
            var user = new User(TelegramId, TelegramId, "Tester", null, now);
            user.SetTimeZone("UTC+03:00", now);
            user.SetReminderProfile(mode == ReminderMode.Ordinary
                ? new ReminderProfile(mode, 0, new(7, 30), OverdueMode.Daily) : ReminderProfile.DefaultFor(mode), now);
            var payment = RecurringPayment.Create(user.Id, "Интернет", 123, "RUB", 1, RecurrenceUnit.Month, Due, now);
            db.AddRange(user, payment);
            if (anotherPayment)
                db.Add(RecurringPayment.Create(user.Id, "Телефон", 321, "RUB", 1, RecurrenceUnit.Month, Due.AddDays(1), now));
            await db.SaveChangesAsync();
            return (user, payment);
        }

        internal async Task CheckAsync(Func<PersonalAssistantDbContext, Task> action)
        {
            await using var db = new PersonalAssistantDbContext(options);
            await action(db);
        }

        internal Task PassAsync() =>
            new ReminderBackgroundService(services.GetRequiredService<IServiceScopeFactory>(), client,
                NullLogger<ReminderBackgroundService>.Instance).RunPassAsync(Token);

        internal Task TypeAsync(string text) => polling.HandleUpdateAsync(client, new Update
        {
            Id = updateId++,
            Message = new Message { Id = updateId, From = sender, Chat = PrivateChat(), Text = text }
        }, Token);

        internal Task TapAsync(string label) => PressAsync(Current.Callback(label), Current.Id);

        internal Task PressAsync(string data, int messageId) => polling.HandleUpdateAsync(client, new Update
        {
            Id = updateId++,
            CallbackQuery = new CallbackQuery
            {
                Id = updateId.ToString(),
                From = sender,
                Message = new Message { Id = messageId, Chat = PrivateChat() },
                Data = data
            }
        }, Token);

        internal async Task OpenPaymentReminderSettingsAsync()
        {
            await TypeAsync("/payments");
            var card = transport.Messages.Values.Last(x => x.Text.Contains("Интернет") && x.Buttons.ContainsKey("✏️ Изменить"));
            await PressAsync(card.Callback("✏️ Изменить"), card.Id);
            await TapAsync("🔔 Напоминания");
        }

        private static Chat PrivateChat() => new() { Id = TelegramId, Type = ChatType.Private };

        public async ValueTask DisposeAsync()
        {
            polling.Dispose();
            http.Dispose();
            await services.DisposeAsync();
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record MessageView(int Id, string Text, Dictionary<string, string> Buttons, IReadOnlyList<string> ButtonRows)
    {
        internal string Callback(string label) => Buttons.TryGetValue(label, out var callback)
            ? callback : throw new InvalidOperationException($"No button '{label}' on: {Text}");
    }

    private sealed class FakeTelegram : HttpMessageHandler
    {
        internal Dictionary<int, MessageView> Messages { get; } = [];
        internal MessageView? Last { get; private set; }
        internal int Sends { get; private set; }
        internal bool FailNextEdit { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = request.RequestUri!.Segments.Last().ToLowerInvariant();
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var requestData = json.RootElement;
            if (method == "answercallbackquery")
                return Response(HttpStatusCode.OK, new { ok = true, result = true });
            if (method == "editmessagetext" && FailNextEdit)
            {
                FailNextEdit = false;
                return Response(HttpStatusCode.BadRequest, new { ok = false, error_code = 400, description = "Bad Request: message to edit not found" });
            }
            Assert.Contains(method, new[] { "sendmessage", "editmessagetext" });
            var id = method == "sendmessage" ? 1000 + ++Sends : requestData.GetProperty("message_id").GetInt32();
            var text = requestData.GetProperty("text").GetString()!;
            var buttons = new Dictionary<string, string>();
            var rows = new List<string>();
            if (requestData.TryGetProperty("reply_markup", out var markup) && markup.TryGetProperty("inline_keyboard", out var keyboard))
            {
                foreach (var row in keyboard.EnumerateArray())
                {
                    var labels = new List<string>();
                    foreach (var button in row.EnumerateArray())
                    {
                        var label = button.GetProperty("text").GetString()!;
                        buttons.Add(label, button.GetProperty("callback_data").GetString()!);
                        labels.Add($"[{label}]");
                    }
                    rows.Add(string.Join(' ', labels));
                }
            }
            Messages[id] = Last = new MessageView(id, text, buttons, rows);
            return Response(HttpStatusCode.OK, new { ok = true, result = new { message_id = id, date = 1797784200, chat = new { id = TelegramId, type = "private" }, text } });
        }

        private static HttpResponseMessage Response(HttpStatusCode status, object body) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
