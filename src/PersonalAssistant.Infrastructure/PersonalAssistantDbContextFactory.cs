using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PersonalAssistant.Infrastructure;

public sealed class PersonalAssistantDbContextFactory : IDesignTimeDbContextFactory<PersonalAssistantDbContext>
{
    public PersonalAssistantDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Database=personalassistant;Username=personalassistant";
        var options = new DbContextOptionsBuilder<PersonalAssistantDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new PersonalAssistantDbContext(options);
    }
}
