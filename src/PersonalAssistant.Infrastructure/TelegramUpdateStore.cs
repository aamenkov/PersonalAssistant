using Microsoft.EntityFrameworkCore;
using PersonalAssistant.Application;

namespace PersonalAssistant.Infrastructure;

public sealed class ProcessedTelegramUpdate
{
    private ProcessedTelegramUpdate() { }

    public int UpdateId { get; private set; }
    public DateTime ClaimedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    public static ProcessedTelegramUpdate Create(int updateId, DateTime claimedAtUtc) => new()
    {
        UpdateId = updateId,
        ClaimedAtUtc = claimedAtUtc
    };

    public void Complete(DateTime completedAtUtc) => CompletedAtUtc = completedAtUtc;
}

public sealed class TelegramUpdateStore(PersonalAssistantDbContext db) : ITelegramUpdateStore
{
    private static readonly TimeSpan StaleClaimTimeout = TimeSpan.FromMinutes(10);

    public async Task<bool> TryBeginAsync(int updateId, DateTime claimedAtUtc, CancellationToken cancellationToken)
    {
        var existing = await db.ProcessedTelegramUpdates.SingleOrDefaultAsync(x => x.UpdateId == updateId, cancellationToken);
        if (existing is not null)
        {
            if (existing.CompletedAtUtc.HasValue || claimedAtUtc - existing.ClaimedAtUtc < StaleClaimTimeout)
                return false;

            await db.ProcessedTelegramUpdates.Where(x => x.UpdateId == updateId).ExecuteDeleteAsync(cancellationToken);
        }

        await db.ProcessedTelegramUpdates.AddAsync(ProcessedTelegramUpdate.Create(updateId, claimedAtUtc), cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task CompleteAsync(int updateId, DateTime completedAtUtc, CancellationToken cancellationToken)
    {
        var update = await db.ProcessedTelegramUpdates.SingleOrDefaultAsync(x => x.UpdateId == updateId, cancellationToken);
        if (update is null)
            return;

        update.Complete(completedAtUtc);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AbandonAsync(int updateId, CancellationToken cancellationToken)
    {
        await db.ProcessedTelegramUpdates
            .Where(x => x.UpdateId == updateId && x.CompletedAtUtc == null)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
