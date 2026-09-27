// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Queue;
using OpenTube.TestSupport;
using OpenTube.Worker.Jobs;
using OpenTube.Worker.Tests.Support;

namespace OpenTube.Worker.Tests.Jobs;

/// <summary>
/// Agendamento inicial da manutenção do analytics. O worker sobe junto com a aplicação, que é
/// quem aplica as migrações, então na primeira partida o esquema pode não existir ainda.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AnalyticsBootstrapperTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly FakeTimeProvider _relogio = new(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private ServiceProvider Montar(string? conexao = null)
    {
        var servicos = new ServiceCollection();

        servicos.AddDbContext<OpenTubeDbContext>(o => o
            .UseNpgsql(conexao ?? postgres.ConnectionString)
            .UseSnakeCaseNamingConvention());

        servicos.AddSingleton<TimeProvider>(_relogio);
        servicos.AddScoped<IJobQueue, PostgresJobQueue>();

        return servicos.BuildServiceProvider();
    }

    private static AnalyticsBootstrapper Criar(IServiceProvider provider) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AnalyticsBootstrapper>.Instance);

    [Fact]
    public async Task Agenda_a_manutencao_na_primeira_partida()
    {
        await using var provider = Montar();
        var agendador = Criar(provider);

        await agendador.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await agendador.StopAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        var job = await db.ProcessingJobs.SingleAsync();

        Assert.Equal(JobKind.AnalyticsRollup, job.Kind);
        Assert.Equal(JobStatus.Pending, job.Status);
    }

    [Fact]
    public async Task Nao_duplica_quando_ja_existe_uma_agendada()
    {
        await using (var db = postgres.CreateContext())
        {
            await new PostgresJobQueue(db, _relogio).EnqueueAsync(JobKind.AnalyticsRollup);
        }

        await using var provider = Montar();
        var agendador = Criar(provider);

        await agendador.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await agendador.StopAsync(CancellationToken.None);

        await using var leitura = postgres.CreateContext();
        Assert.Equal(1, await leitura.ProcessingJobs.CountAsync());
    }

    [Fact]
    public async Task Insiste_enquanto_o_banco_nao_responde()
    {
        // Aponta para uma porta sem ninguém escutando: é o que acontece quando o worker
        // sobe antes de a aplicação terminar de migrar.
        await using var provider = Montar("Host=localhost;Port=1;Database=x;Username=x;Password=x;Timeout=1");
        var agendador = Criar(provider);

        await agendador.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        // Desistir na primeira tentativa deixaria o analytics parado para sempre; o esperado
        // é que ele continue tentando até o encerramento.
        await agendador.StopAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        Assert.Empty(await db.ProcessingJobs.ToListAsync());
    }

    [Fact]
    public async Task O_encerramento_interrompe_as_tentativas()
    {
        await using var provider = Montar("Host=localhost;Port=1;Database=x;Username=x;Password=x;Timeout=1");
        var agendador = Criar(provider);

        await agendador.StartAsync(CancellationToken.None);

        using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await agendador.StopAsync(prazo.Token);

        Assert.False(prazo.IsCancellationRequested, "o encerramento deveria ser imediato");
    }
}
