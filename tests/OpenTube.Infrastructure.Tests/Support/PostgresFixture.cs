using Microsoft.EntityFrameworkCore;
using OpenTube.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace OpenTube.Infrastructure.Tests.Support;

/// <summary>
/// Sobe um PostgreSQL efêmero para a suíte inteira. Testar persistência contra um banco real
/// é o único jeito de exercitar gatilho, índice e <c>SKIP LOCKED</c>, que não existem em
/// substitutos em memória.
/// </summary>
public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("opentube_testes")
        .WithUsername("opentube")
        .WithPassword("opentube")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public OpenTubeDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<OpenTubeDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new OpenTubeDbContext(options);
    }

    /// <summary>Esvazia as tabelas entre testes, preservando o esquema já migrado.</summary>
    public async Task ResetAsync()
    {
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE videos, video_assets, users, processing_jobs RESTART IDENTITY CASCADE;");
    }
}

[CollectionDefinition(Name)]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
