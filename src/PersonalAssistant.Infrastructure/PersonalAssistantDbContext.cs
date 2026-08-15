using Microsoft.EntityFrameworkCore;
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
        });

        modelBuilder.Entity<RecurringPayment>(entity =>
        {
            entity.HasKey(x => x.Id);
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

        modelBuilder.Entity<Reminder>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.RecurringPaymentId, x.DueDate, x.LocalDate, x.Kind }).IsUnique();
            entity.HasOne(x => x.RecurringPayment).WithMany(x => x.Reminders).HasForeignKey(x => x.RecurringPaymentId).OnDelete(DeleteBehavior.Cascade);
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
    public Task<User?> FindByTelegramUserIdAsync(long telegramUserId, CancellationToken cancellationToken) =>
        db.Users.SingleOrDefaultAsync(x => x.TelegramUserId == telegramUserId, cancellationToken);

    public Task AddAsync(User user, CancellationToken cancellationToken) => db.Users.AddAsync(user, cancellationToken).AsTask();

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

public sealed class PaymentRepository(PersonalAssistantDbContext db) : IPaymentRepository
{
    public Task AddAsync(RecurringPayment payment, CancellationToken cancellationToken) => db.RecurringPayments.AddAsync(payment, cancellationToken).AsTask();

    public Task AddTransactionAsync(PaymentTransaction transaction, CancellationToken cancellationToken) =>
        db.PaymentTransactions.AddAsync(transaction, cancellationToken).AsTask();

    public async Task<IReadOnlyList<ReminderCandidate>> GetReminderCandidatesAsync(CancellationToken cancellationToken)
    {
        return await db.RecurringPayments
            .AsNoTracking()
            .Where(x => x.IsActive && x.NextPaymentDate.HasValue)
            .Select(x => new ReminderCandidate(x.Id, x.UserId, x.User.TelegramChatId, x.Name, x.Amount, x.Currency,
                x.NextPaymentDate!.Value, x.IsAutoDebit, x.User.TimeZoneId, x.User.ReminderTimeLocal, x.User.ReminderDaysBefore))
            .ToListAsync(cancellationToken);
    }

    public Task<RecurringPayment?> FindForOwnerAsync(Guid userId, Guid paymentId, CancellationToken cancellationToken) =>
        db.RecurringPayments.SingleOrDefaultAsync(x => x.UserId == userId && x.Id == paymentId, cancellationToken);

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

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new PaymentConcurrencyException(exception);
        }
    }
}

public sealed class ReminderRepository(PersonalAssistantDbContext db) : IReminderRepository
{
    public async Task<bool> TryClaimAsync(Guid paymentId, DateOnly dueDate, DateOnly localDate, ReminderKind kind, DateTime claimedAtUtc, CancellationToken cancellationToken)
    {
        if (await db.Reminders.AnyAsync(x => x.RecurringPaymentId == paymentId && x.DueDate == dueDate && x.LocalDate == localDate && x.Kind == kind, cancellationToken))
            return false;

        await db.Reminders.AddAsync(Reminder.Create(paymentId, dueDate, localDate, kind, claimedAtUtc), cancellationToken);
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
}

public sealed class ConversationStateRepository(PersonalAssistantDbContext db) : IConversationStateRepository
{
    public Task<ConversationState?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        db.ConversationStates.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);

    public Task AddAsync(ConversationState state, CancellationToken cancellationToken) => db.ConversationStates.AddAsync(state, cancellationToken).AsTask();

    public void Remove(ConversationState state) => db.ConversationStates.Remove(state);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
