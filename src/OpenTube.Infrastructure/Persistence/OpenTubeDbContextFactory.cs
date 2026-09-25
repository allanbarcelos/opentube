using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenTube.Infrastructure.Persistence;

/// <summary>
/// Usado apenas pelas ferramentas de linha de comando do EF Core para criar migrações sem
/// precisar subir a aplicação inteira.
/// </summary>
public class OpenTubeDbContextFactory : IDesignTimeDbContextFactory<OpenTubeDbContext>
{
    public OpenTubeDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("OPENTUBE_DB")
            ?? "Host=localhost;Port=5432;Database=opentube;Username=opentube;Password=opentube";

        var options = new DbContextOptionsBuilder<OpenTubeDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(OpenTubeDbContext).Assembly.FullName))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new OpenTubeDbContext(options);
    }
}
