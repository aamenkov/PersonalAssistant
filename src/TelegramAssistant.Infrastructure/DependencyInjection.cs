using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TelegramAssistant.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddTelegramAssistantInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");
        return services.AddDbContext<TelegramAssistantDbContext>(options => options.UseNpgsql(connectionString));
    }
}
