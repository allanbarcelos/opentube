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
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
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

    /// <summary>
    /// Esvazia as tabelas entre testes, preservando o esquema já migrado. A lista é montada a
    /// partir do catálogo para que uma tabela nova não fique de fora e vaze estado entre testes.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            """
            DO $$
            DECLARE tabelas text;
            BEGIN
                SELECT string_agg(format('%I.%I', schemaname, tablename), ', ')
                  INTO tabelas
                  FROM pg_tables
                 WHERE schemaname = 'public'
                   AND tablename <> '__EFMigrationsHistory';

                IF tabelas IS NOT NULL THEN
                    EXECUTE 'TRUNCATE ' || tabelas || ' RESTART IDENTITY CASCADE';
                END IF;
            END $$;
            """);
    }
}

