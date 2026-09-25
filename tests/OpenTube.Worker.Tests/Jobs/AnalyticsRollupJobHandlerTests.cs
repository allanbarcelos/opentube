using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Analytics;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Queue;
using OpenTube.Infrastructure.Storage;
using OpenTube.Shared.Analytics;
using OpenTube.TestSupport;
using OpenTube.Worker.Jobs;
using OpenTube.Worker.Tests.Support;

namespace OpenTube.Worker.Tests.Jobs;

/// <summary>Manutenção periódica do analytics executada pelo worker.</summary>
[Collection(IntegrationCollection.Name)]
public class AnalyticsRollupJobHandlerTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const double Duracao = 600;

    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();

    private readonly FakeTimeProvider _relogio = new(Agora);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private (AnalyticsRollupJobHandler Executor, IJobQueue Fila, OpenTubeDbContext Db) Criar(AnalyticsOptions? opcoes = null)
    {
        var db = postgres.CreateContext();
        var fila = new PostgresJobQueue(db, _relogio);

        var executor = new AnalyticsRollupJobHandler(
            new AnalyticsCollector(db, _relogio, NullLogger<AnalyticsCollector>.Instance),
            new AnalyticsAggregator(db, _relogio, NullLogger<AnalyticsAggregator>.Instance),
            fila,
            Microsoft.Extensions.Options.Options.Create(opcoes ?? new AnalyticsOptions()),
            _relogio,
            NullLogger<AnalyticsRollupJobHandler>.Instance);

        return (executor, fila, db);
    }

    private static QueuedJob Job() => new(Guid.CreateVersion7(), JobKind.AnalyticsRollup, null, "{}", 1);

    private async Task<Video> CriarVideoComAudienciaAsync()
    {
        Video video;

        await using (var db = postgres.CreateContext())
        {
            var videoId = Guid.CreateVersion7();

            video = Video.CreateDraft("Reunião", $"v-{videoId:n}"[..20], "originals/a.mp4", Admin, Agora, id: videoId);
            video.MarkUploaded(1024);
            video.StartProcessing();
            video.MarkReady(StorageKeys.VodPrefix(videoId), Duracao, 1280, 720, null, null, Agora);

            db.Videos.Add(video);
            await db.SaveChangesAsync();
        }

        await using (var db = postgres.CreateContext())
        {
            var coletor = new AnalyticsCollector(db, _relogio, NullLogger<AnalyticsCollector>.Instance);
            var sessao = await coletor.StartAsync(video.Id, null, "visitante-1", null, null, null, null);

            await coletor.RecordAsync(sessao.Id, null, "visitante-1",
                new PlaybackBatch([new PlaybackEventReport("progress", Duracao, 0, Duracao)]));
        }

        return video;
    }

    [Fact]
    public async Task Consolida_os_numeros_e_a_curva()
    {
        var video = await CriarVideoComAudienciaAsync();
        var (executor, _, db) = Criar();
        await using var _1 = db;

        await executor.HandleAsync(Job());

        await using var leitura = postgres.CreateContext();

        Assert.Single(await leitura.VideoDailyStats.ToListAsync());
        Assert.Equal(100, await leitura.VideoRetentionBuckets.CountAsync(b => b.VideoId == video.Id));
    }

    [Fact]
    public async Task Fecha_as_sessoes_sem_sinal()
    {
        await CriarVideoComAudienciaAsync();
        var (executor, _, db) = Criar();
        await using var _1 = db;

        _relogio.Advance(AnalyticsCollector.InactivityTimeout + TimeSpan.FromMinutes(1));
        await executor.HandleAsync(Job());

        await using var leitura = postgres.CreateContext();
        Assert.All(await leitura.PlaybackSessions.ToListAsync(), s => Assert.NotNull(s.EndedAt));
    }

    [Fact]
    public async Task Se_reagenda_ao_terminar()
    {
        var (executor, _, db) = Criar(new AnalyticsOptions { RollupInterval = TimeSpan.FromMinutes(5) });
        await using var _1 = db;

        await executor.HandleAsync(Job());

        await using var leitura = postgres.CreateContext();
        var proximo = await leitura.ProcessingJobs.SingleAsync(j => j.Kind == JobKind.AnalyticsRollup);

        Assert.Equal(JobStatus.Pending, proximo.Status);
        Assert.Equal(Agora.AddMinutes(5), proximo.RunAfter);
    }

    [Fact]
    public async Task Garante_a_particao_do_mes_seguinte()
    {
        var (executor, _, db) = Criar();
        await using var _1 = db;

        await executor.HandleAsync(Job());

        await using var leitura = postgres.CreateContext();
        var particoes = await leitura.Database
            .SqlQuery<string>($"SELECT relname AS \"Value\" FROM pg_class WHERE relname LIKE 'playback_events_2026_10'")
            .ToListAsync();

        Assert.Single(particoes);
    }

    [Fact]
    public async Task Descarta_evento_bruto_vencido()
    {
        await CriarVideoComAudienciaAsync();
        var (executor, _, db) = Criar(new AnalyticsOptions { EventRetention = TimeSpan.FromDays(30) });
        await using var _1 = db;

        // Primeira passagem consolida o dia em que houve audiência.
        await executor.HandleAsync(Job());

        _relogio.Advance(TimeSpan.FromDays(60));
        await executor.HandleAsync(Job());

        await using var leitura = postgres.CreateContext();

        Assert.Empty(await leitura.PlaybackEvents.ToListAsync());
        // O que se perde é a investigação pontual; o histórico de audiência permanece.
        Assert.NotEmpty(await leitura.VideoDailyStats.ToListAsync());
    }
}
