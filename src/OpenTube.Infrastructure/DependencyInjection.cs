using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Queue;

namespace OpenTube.Infrastructure;

/// <summary>Registro dos serviços de infraestrutura compartilhados pela aplicação e pelo worker.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddOpenTubeInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("Default")
            ?? configuration["OPENTUBE_DB"]
            ?? throw new InvalidOperationException("Cadeia de conexão do banco não configurada (ConnectionStrings:Default ou OPENTUBE_DB).");

        services.AddDbContext<OpenTubeDbContext>(options => options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(OpenTubeDbContext).Assembly.FullName))
            .UseSnakeCaseNamingConvention());

        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddTimeProvider();
        services.AddScoped<IJobQueue, PostgresJobQueue>();

        return services;
    }

    private static IServiceCollection TryAddTimeProvider(this IServiceCollection services)
    {
        if (services.All(s => s.ServiceType != typeof(TimeProvider)))
            services.AddSingleton(TimeProvider.System);

        return services;
    }
}
