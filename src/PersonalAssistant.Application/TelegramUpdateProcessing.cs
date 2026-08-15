namespace PersonalAssistant.Application;

public interface ITelegramUpdateStore
{
    Task<bool> TryBeginAsync(int updateId, DateTime claimedAtUtc, CancellationToken cancellationToken);
    Task CompleteAsync(int updateId, DateTime completedAtUtc, CancellationToken cancellationToken);
    Task AbandonAsync(int updateId, CancellationToken cancellationToken);
}
