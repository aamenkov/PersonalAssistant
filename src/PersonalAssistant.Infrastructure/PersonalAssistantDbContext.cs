using Microsoft.EntityFrameworkCore;
using Npgsql;
using PersonalAssistant.Application;
using PersonalAssistant.Domain;

namespace PersonalAssistant.Infrastructure;

public sealed class PersonalAssistantDbContext(DbContextOptions<PersonalAssistantDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RecurringPayment> RecurringPayments => Set<RecurringPayment>();
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<ReminderSnooze> ReminderSnoozes => Set<ReminderSnooze>();
    public DbSet<ProcessedTelegramUpdate> ProcessedTelegramUpdates => Set<ProcessedTelegramUpdate>();
    public DbSet<ConversationState> ConversationStates => Set<ConversationState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TelegramUserId).IsUnique();
            entity.Property(x => x.DefaultCurrency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.IsTimeZoneConfigured).IsRequired();
            entity.Property(x => x.ReminderTimeLocal).IsRequired();
            entity.Property(x => x.ReminderDaysBefore).IsRequired();
            entity.Property(x => x.ReminderMode).HasDefaultValue(ReminderMode.Ordinary);
            entity.Property(x => x.ReminderOverdueMode).HasDefaultValue(OverdueMode.Daily);
        });

        modelBuilder.Entity<RecurringPayment>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.NextPaymentDate).IsConcurrencyToken();
            entity.Property(x => x.IsActive).IsConcurrencyToken();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.ScheduleDayOfMonth).IsRequired();
            entity.Property(x => x.IsLastDayOfMonth).IsRequired();
            entity.HasIndex(x => new { x.UserId, x.IsActive });
            entity.HasOne(x => x.User).WithMany(x => x.Payments).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PaymentTransaction>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ExpectedAmount).HasPrecision(18, 2);
            entity.Property(x => x.PaidAmount).HasPrecision(18, 2);
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.PaidPeriod).HasMaxLength(20).IsRequired();
            entity.HasIndex(x => new { x.RecurringPaymentId, x.PaidPeriod }).IsUnique();
            entity.HasOne(x => x.RecurringPayment).WithMany(x => x.Transactions).HasForeignKey(x => x.RecurringPaymentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PaymentReminderSettings>(entity =>
        {
            entity.HasKey(x => x.RecurringPaymentId);
            entity.Property(x => x.RecurringPaymentId).ValueGeneratedNever();
            entity.HasOne<RecurringPayment>().WithOne(x => x.ReminderSettings)
                .HasForeignKey<PaymentReminderSettings>(x => x.RecurringPaymentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.OwnsOne(x => x.Profile, profile =>
            {
                profile.Property(x => x.Mode);
                profile.Property(x => x.DaysBefore);
                profile.Property(x => x.ReminderTimeLocal);
                profile.Property(x => x.OverdueMode);
                profile.Property(x => x.CriticalIntervalMinutes);
                profile.Property(x => x.CriticalWindowStart);
                profile.Property(x => x.CriticalWindowEnd);
            });
        });

        modelBuilder.Entity<Reminder>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SlotKey).HasMaxLength(48).HasDefaultValue(ReminderDispatch.DailySlot);
            entity.Property(x => x.Status).HasDefaultValue(ReminderDeliveryStatus.HistoricalUnknown);
            entity.HasIndex(x => new { x.RecurringPaymentId, x.DueDate, x.LocalDate, x.Kind, x.SlotKey }).IsUnique();
            entity.HasIndex(x => new { x.Status, x.LeaseUntilUtc });
            entity.HasOne(x => x.RecurringPayment).WithMany(x => x.Reminders).HasForeignKey(x => x.RecurringPaymentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReminderSnooze>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.RecurringPaymentId, x.DueDate }).IsUnique();
            entity.HasOne(x => x.RecurringPayment).WithMany().HasForeignKey(x => x.RecurringPaymentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProcessedTelegramUpdate>(entity =>
        {
            entity.HasKey(x => x.UpdateId);
            entity.Property(x => x.UpdateId).ValueGeneratedNever();
            entity.Property(x => x.ClaimedAtUtc).IsRequired();
        });

        modelBuilder.Entity<ConversationState>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.UserId).IsUnique();
            entity.Property(x => x.PayloadJson).IsRequired();
            entity.HasOne(x => x.User).WithOne(x => x.ConversationState).HasForeignKey<ConversationState>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class UserRepository(PersonalAssistantDbContext db) : IUserRepository
{
    public async Task<bool> TrySetReminderProfileAsync(long telegramUserId, ReminderProfile original, ReminderProfile profile,
        DateTime utcNow, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"Users\" WHERE \"TelegramUserId\" = {telegramUserId} FOR UPDATE", cancellationToken);
        var user = await FindByTelegramUserIdAsync(telegramUserId, cancellationToken);
        if (user is null)
            return false;
        await db.Entry(user).ReloadAsync(cancellationToken);
        if (user.GetReminderProfile() != original)
            return false;
        user.SetReminderProfile(profile, utcNow);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
    public Task<User?> FindByTelegramUserIdAsync(long telegramUserId, CancellationToken cancellationToken) =>
        db.Users.SingleOrDefaultAsync(x => x.TelegramUserId == telegramUserId, cancellationToken);

    public Task AddAsync(User user, CancellationToken cancellationToken) => db.Users.AddAsync(user, cancellationToken).AsTask();

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

public sealed class PaymentRepository(PersonalAssistantDbContext db) : IPaymentRepository, IPaymentReminderSettingsRepository
{
    public Task AddAsync(RecurringPayment payment, CancellationToken cancellationToken) => db.RecurringPayments.AddAsync(payment, cancellationToken).AsTask();

    public Task AddTransactionAsync(PaymentTransaction transaction, CancellationToken cancellationToken) =>
        db.PaymentTransactions.AddAsync(transaction, cancellationToken).AsTask();

    public Task<RecurringPayment?> FindForOwnerAsync(Guid userId, Guid paymentId, CancellationToken cancellationToken) =>
        db.RecurringPayments.SingleOrDefaultAsync(x => x.UserId == userId && x.Id == paymentId, cancellationToken);

    public Task<RecurringPayment?> FindPaymentWithReminderSettingsAsync(Guid userId, Guid paymentId, CancellationToken cancellationToken) =>
        db.RecurringPayments.Include(x => x.User).Include(x => x.ReminderSettings)
            .SingleOrDefaultAsync(x => x.UserId == userId && x.Id == paymentId, cancellationToken);

    public async Task<IReadOnlyList<ReminderPreviewPayment>> ListExamplesAsync(Guid userId, int offset, int limit, CancellationToken cancellationToken) =>
        await db.RecurringPayments.AsNoTracking().Where(x => x.UserId == userId && x.IsActive && x.NextPaymentDate.HasValue)
            .OrderBy(x => x.NextPaymentDate).ThenBy(x => x.Id).Skip(offset).Take(limit)
            .Select(x => new ReminderPreviewPayment(x.Id, x.Name, x.NextPaymentDate!.Value)).ToListAsync(cancellationToken);

    public async Task<bool> TryChangeReminderProfileAsync(Guid userId, Guid paymentId, EffectiveReminderProfile original,
        EffectiveReminderProfile target, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Both inherited and individual writes use the same lock order: owner, then payment.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"RecurringPayments\" WHERE \"Id\" = {paymentId} AND \"UserId\" = {userId} FOR UPDATE", cancellationToken);
        var current = await db.RecurringPayments.AsNoTracking().Include(x => x.User).Include(x => x.ReminderSettings)
            .SingleOrDefaultAsync(x => x.Id == paymentId && x.UserId == userId && x.IsActive, cancellationToken);
        if (current is null || EffectiveReminderProfile.From(current) != original)
            return false;
        // This operation uses a fresh handler scope; its draft and user may already be tracked, but not the payment.
        var payment = await FindPaymentWithReminderSettingsAsync(userId, paymentId, cancellationToken);
        if (payment is null)
            return false;
        if (target.IsInherited)
            payment.UseUserReminderProfile();
        else if (target.Profile is { } profile)
            payment.SetReminderProfile(profile);
        else
            payment.DisableReminders();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<RecurringPayment>> GetActiveAsync(Guid userId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var query = db.RecurringPayments
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.IsActive);

        if (from.HasValue)
            query = query.Where(x => x.NextPaymentDate >= from.Value);
        if (to.HasValue)
            query = query.Where(x => x.NextPaymentDate <= to.Value);

        return await query.OrderBy(x => x.NextPaymentDate).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PaymentTransaction>> GetTransactionsForOwnerAsync(Guid userId, Guid? paymentId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var query = db.PaymentTransactions
            .AsNoTracking()
            .Include(x => x.RecurringPayment)
            .Where(x => x.RecurringPayment.UserId == userId);

        if (paymentId.HasValue)
            query = query.Where(x => x.RecurringPaymentId == paymentId.Value);
        if (from.HasValue)
            query = query.Where(x => x.PaidDate >= from.Value);
        if (to.HasValue)
            query = query.Where(x => x.PaidDate <= to.Value);

        return await query.OrderByDescending(x => x.PaidDate).ToListAsync(cancellationToken);
    }

    public Task<bool> HasTransactionForPeriodAsync(Guid userId, Guid paymentId, string paidPeriod, CancellationToken cancellationToken) =>
        db.PaymentTransactions.AnyAsync(x => x.RecurringPaymentId == paymentId
            && x.PaidPeriod == paidPeriod
            && x.RecurringPayment.UserId == userId, cancellationToken);

    public Task<int> ClearTransactionHistoryAsync(Guid userId, CancellationToken cancellationToken) =>
        db.PaymentTransactions
            .Where(x => x.RecurringPayment.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

    public async Task<int> DeleteAllPaymentsAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Transactions use Restrict because ordinary payment edits must never
        // remove history. Delete them explicitly before deleting payments.
        await db.PaymentTransactions
            .Where(x => x.RecurringPayment.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        var deletedPayments = await db.RecurringPayments
            .Where(x => x.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deletedPayments;
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception is DbUpdateConcurrencyException ||
            exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "IX_PaymentTransactions_RecurringPaymentId_PaidPeriod"
            })
        {
            // The database rolled this operation back. Do not carry its pending writes
            // into the next SaveChanges (for example, cleanup of a finished payment dialog).
            db.ChangeTracker.Clear();
            throw new PaymentConcurrencyException(exception);
        }
    }
}

public sealed class ConversationStateRepository(PersonalAssistantDbContext db) : IConversationStateRepository
{
    public Task<ConversationState?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        db.ConversationStates.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);

    public Task AddAsync(ConversationState state, CancellationToken cancellationToken) => db.ConversationStates.AddAsync(state, cancellationToken).AsTask();

    public void Remove(ConversationState state) => db.ConversationStates.Remove(state);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
