using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PersonalAssistant.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPersonalAssistantInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");
        return services.AddDbContext<PersonalAssistantDbContext>(options => options.UseNpgsql(connectionString));
    }
}
