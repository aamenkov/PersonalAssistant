using PersonalAssistant.Application;

namespace PersonalAssistant.UnitTests;

public sealed class UserUpdateGateTests
{
    [Fact]
    public async Task SameUserUpdates_AreProcessedSequentially()
    {
        var gate = new UserUpdateGate();
        var active = 0;
        var maximumActive = 0;

        async Task HandleAsync()
        {
            var current = Interlocked.Increment(ref active);
            Interlocked.Exchange(ref maximumActive, Math.Max(maximumActive, current));
            await Task.Delay(50);
            Interlocked.Decrement(ref active);
        }

        await Task.WhenAll(
            gate.RunAsync(42, HandleAsync, CancellationToken.None),
            gate.RunAsync(42, HandleAsync, CancellationToken.None));

        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task DifferentUsers_AreNotBlockedByEachOther()
    {
        var gate = new UserUpdateGate();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = gate.RunAsync(1, () => releaseFirst.Task, CancellationToken.None);
        var second = gate.RunAsync(2, () =>
        {
            secondStarted.SetResult();
            return Task.CompletedTask;
        }, CancellationToken.None);

        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);
    }
}
