using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PersonalAssistant.Application;
using PersonalAssistant.Bot;
using PersonalAssistant.Domain;
using Telegram.Bot;

namespace PersonalAssistant.UnitTests;

public sealed class ReminderWorkerTests
{
    private static readonly DateOnly DueDate = new(2026, 12, 20);
    private static readonly DateTimeOffset Now = new(2026, 12, 20, 16, 30, 0, TimeSpan.Zero);

    [Fact]
    public void DueSelection_UsesLatestCriticalSlotAndKeepsDailyCatchupUntilMidnight()
    {
        // A late pass selects one slot and respects the end minute of the critical window.
        var critical = MakeCandidate(1, ReminderMode.Critical);
        Assert.Equal(new TimeOnly(16, 0), ReminderService.SelectDue(critical, Now)?.Key.CriticalTime);
        Assert.Equal(new TimeOnly(21, 0), ReminderService.SelectDue(critical, Now.AddHours(4).AddMinutes(30).AddSeconds(59))?.Key.CriticalTime);
        Assert.Null(ReminderService.SelectDue(critical, Now.AddHours(4).AddMinutes(31)));

        var daily = MakeCandidate(2, ReminderMode.Ordinary);
        Assert.Equal(ReminderKind.DueToday, ReminderService.SelectDue(daily, Now.AddHours(7))?.Key.Kind);
        Assert.Null(ReminderService.SelectDue(daily, Now.AddHours(8)));
    }

    [Fact]
    public async Task Pass_Records429AndTimeoutAndStillDeliversRemainingPayment()
    {
        // Exercise the real Telegram client adapter without network access and verify each outcome.
        var candidates = new[] { MakeCandidate(1, ReminderMode.Ordinary), MakeCandidate(2, ReminderMode.Ordinary), MakeCandidate(3, ReminderMode.Ordinary) };
        var completed = new List<Completion>();
        var scopes = 0;
        using var transport = new FakeTelegram();
        using var http = new HttpClient(transport);
        var client = new TelegramBotClient(new TelegramBotClientOptions("123456:test_only") { RetryCount = 0 }, http);
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(new FixedClock())
            .AddKeyedSingleton<ITelegramBotClient>(ReminderBackgroundService.BotClientKey, client)
            .AddScoped<IReminderRepository>(_ =>
            {
                scopes++;
                return new FakeRepository(candidates, completed);
            })
            .AddScoped<ReminderService>()
            .AddHostedService<ReminderBackgroundService>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var worker = Assert.IsType<ReminderBackgroundService>(Assert.Single(services.GetServices<IHostedService>()));

        await worker.RunPassAsync(CancellationToken.None);

        Assert.Equal(1, scopes);
        Assert.Equal(3, transport.Calls);
        Assert.Collection(completed,
            rejected =>
            {
                Assert.Equal(ReminderSendOutcome.Rejected, rejected.Result.Outcome);
                Assert.Equal(Now.UtcDateTime.AddMinutes(2), rejected.RetryAtUtc);
            },
            unknown =>
            {
                Assert.Equal(ReminderSendOutcome.Unknown, unknown.Result.Outcome);
                Assert.Null(unknown.RetryAtUtc);
            },
            sent =>
            {
                Assert.Equal(ReminderSendOutcome.Sent, sent.Result.Outcome);
                Assert.Equal(42, sent.Result.MessageId);
            });
        Assert.Contains(TelegramCallbackData.Period("p", candidates[2].PaymentId, DueDate), transport.LastPayload);
        Assert.Contains(TelegramCallbackData.Period("s", candidates[2].PaymentId, DueDate), transport.LastPayload);
    }

    private static ReminderCandidate MakeCandidate(long chatId, ReminderMode mode) =>
        new(Guid.NewGuid(), chatId, "VPS", 100, "RUB", DueDate, false, "UTC", ReminderProfile.DefaultFor(mode), null);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Completion(ReminderSendResult Result, DateTime? RetryAtUtc);

    private sealed class FakeRepository(ReminderCandidate[] candidates, List<Completion> completed) : IReminderRepository
    {
        public Task<IReadOnlyList<ReminderCandidate>> GetCandidatesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReminderCandidate>>(candidates);

        public Task<IReadOnlyList<ReminderDispatch>> FilterReadyAsync(IReadOnlyList<ReminderDispatch> dispatches,
            DateTime utcNow, CancellationToken cancellationToken) => Task.FromResult(dispatches);

        public Task MarkExpiredAttemptsUnknownAsync(DateTime utcNow, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ReminderReservation?> TryReserveAsync(ReminderDispatch dispatch, DateTime utcNow, DateTime leaseUntilUtc,
            CancellationToken cancellationToken) => Task.FromResult<ReminderReservation?>(new(Guid.NewGuid(), Guid.NewGuid(), 0));

        public Task<bool> TryStartAttemptAsync(ReminderDispatch dispatch, ReminderReservation reservation,
            TimeSpan lease, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task CompleteAttemptAsync(ReminderDispatch dispatch, ReminderReservation reservation, ReminderSendResult result,
            DateTime utcNow, DateTime? retryAtUtc, CancellationToken cancellationToken)
        {
            completed.Add(new Completion(result, retryAtUtc));
            return Task.CompletedTask;
        }

        public Task<ReminderCandidate?> GetSnoozeTargetAsync(Guid userId, Guid paymentId, DateOnly dueDate,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> UpsertSnoozeAsync(Guid userId, Guid paymentId, DateOnly dueDate, Guid? expectedSnoozeId, DateTime untilUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> CancelSnoozeAsync(Guid userId, Guid paymentId, DateOnly dueDate, Guid snoozeId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeTelegram : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastPayload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastPayload = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Calls switch
            {
                1 => Respond(HttpStatusCode.TooManyRequests, """
                    {"ok":false,"error_code":429,"description":"Too Many Requests","parameters":{"retry_after":120}}
                    """),
                2 => throw new TaskCanceledException("Simulated network timeout"),
                _ => Respond(HttpStatusCode.OK, """
                    {"ok":true,"result":{"message_id":42,"date":1797784200,"chat":{"id":3,"type":"private"}}}
                    """)
            };
        }

        private static HttpResponseMessage Respond(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
