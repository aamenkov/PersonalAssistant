using System.Collections.Concurrent;

namespace PersonalAssistant.Application;

public sealed class UserUpdateGate
{
    private readonly ConcurrentDictionary<long, SemaphoreSlim> locks = new();

    public async Task RunAsync(long telegramUserId, Func<Task> action, CancellationToken cancellationToken)
    {
        var userLock = locks.GetOrAdd(telegramUserId, static _ => new SemaphoreSlim(1, 1));
        await userLock.WaitAsync(cancellationToken);
        try
        {
            await action();
        }
        finally
        {
            userLock.Release();
        }
    }
}
