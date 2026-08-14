using Microsoft.EntityFrameworkCore;
using PersonalAssistant.Domain;
using PersonalAssistant.Infrastructure;

namespace PersonalAssistant.IntegrationTests;

public sealed class PaymentRepositoryTests
{
    [Fact]
    public async Task AddTransactionAsync_MarksNewTransactionAsAdded()
    {
        var options = new DbContextOptionsBuilder<PersonalAssistantDbContext>()
            .UseNpgsql("Host=localhost;Database=state_check;Username=test;Password=test")
            .Options;
        await using var db = new PersonalAssistantDbContext(options);
        var payment = RecurringPayment.Create(Guid.NewGuid(), "Internet", 400, "RUB", 1,
            RecurrenceUnit.Month, new DateOnly(2026, 8, 15), DateTime.UtcNow);
        db.Attach(payment);
        var transaction = payment.RecordPayment(400, new DateOnly(2026, 8, 14), "2026-08-15", null, DateTime.UtcNow);

        await new PaymentRepository(db).AddTransactionAsync(transaction, CancellationToken.None);

        Assert.Equal(EntityState.Added, db.Entry(transaction).State);
        Assert.Equal(EntityState.Modified, db.Entry(payment).State);
    }
}
