using Microsoft.EntityFrameworkCore;
using TelegramAssistant.Application;
using TelegramAssistant.Domain;

namespace TelegramAssistant.Infrastructure;

public sealed class TelegramAssistantDbContext(DbContextOptions<TelegramAssistantDbContext> options)
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
        });

        modelBuilder.Entity<RecurringPayment>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.HasIndex(x => new { x.UserId, x.IsActive });
            entity.HasOne(x => x.User).WithMany(x => x.Payments).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PaymentTransaction>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PaidAmount).HasPrecision(18, 2);
            entity.Property(x => x.PaidPeriod).HasMaxLength(20).IsRequired();
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

public sealed class UserRepository(TelegramAssistantDbContext db) : IUserRepository
{
    public Task<User?> FindByTelegramUserIdAsync(long telegramUserId, CancellationToken cancellationToken) =>
        db.Users.SingleOrDefaultAsync(x => x.TelegramUserId == telegramUserId, cancellationToken);

    public Task AddAsync(User user, CancellationToken cancellationToken) => db.Users.AddAsync(user, cancellationToken).AsTask();

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
