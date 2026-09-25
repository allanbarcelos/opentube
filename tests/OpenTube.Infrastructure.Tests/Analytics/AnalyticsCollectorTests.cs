using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Analytics;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Storage;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.Shared.Analytics;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Analytics;

[Collection(IntegrationCollection.Name)]
public class AnalyticsCollectorTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const double Duracao = 600;

    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();
    private static readonly Guid Usuario = Guid.CreateVersion7();

    private readonly FakeTimeProvider _relogio = new(Agora);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private (AnalyticsCollector Coletor, OpenTubeDbContext Db) Criar()
    {
        var db = postgres.CreateContext();

        return (new AnalyticsCollector(db, _relogio, NullLogger<AnalyticsCollector>.Instance), db);
    }

    private async Task<Video> CriarVideoAsync()
    {
        await using var db = postgres.CreateContext();
        var videoId = Guid.CreateVersion7();

        var video = Video.CreateDraft("Reunião", $"v-{videoId:n}"[..20], "originals/a.mp4", Admin, Agora, id: videoId);
        video.MarkUploaded(1024);
        video.StartProcessing();
        video.MarkReady(StorageKeys.VodPrefix(videoId), Duracao, 1280, 720, null, null, Agora);

        db.Videos.Add(video);
        await db.SaveChangesAsync();

        return video;
    }

    private static PlaybackBatch Lote(params PlaybackEventReport[] eventos) => new(eventos);

    private static PlaybackEventReport Progresso(double de, double ate) => new("progress", ate, de, ate);

    [Fact]
    public async Task Abre_a_sessao_com_o_perfil_do_aparelho()
    {
        var video = await CriarVideoAsync();
        var (coletor, db) = Criar();
        await using var _ = db;

        var sessao = await coletor.StartAsync(
            video.Id, Usuario, null, null,
            "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1",
            "resumo-do-ip", "https://intranet.exemplo");

        Assert.Equal(DeviceType.Mobile, sessao.Device);
        Assert.Equal("iOS", sessao.OperatingSystem);
        Assert.Equal("Safari", sessao.Browser);
        Assert.Equal("resumo-do-ip", sessao.IpHash);

        await using var leitura = postgres.CreateContext();
        Assert.Equal(1, await leitura.PlaybackEvents.CountAsync(e => e.Type == PlaybackEventType.Start));
    }

    [Fact]
    public async Task Registra_o_trecho_assistido()
    {
        var video = await CriarVideoAsync();
        var (coletor, db) = Criar();
        await using var _ = db;
        var sessao = await coletor.StartAsync(video.Id, Usuario, null, null, null, null, null);

        await coletor.RecordAsync(sessao.Id, Usuario, null, Lote(Progresso(0, 30), Progresso(30, 60)));

        await using var leitura = postgres.CreateContext();
        var gravada = await leitura.PlaybackSessions.Include(s => s.Intervals).SingleAsync();

        Assert.Equal(60, gravada.WatchedSeconds);
        Assert.Single(gravada.Intervals);
    }

    [Fact]
    public async Task Reassistir_nao_infla_o_tempo_gravado()
    {
        var video = await CriarVideoAsync();
        var (coletor, db) = Criar();
        await using var _ = db;
        var sessao = await coletor.StartAsync(video.Id, Usuario, null, null, null, null, null);

        await coletor.RecordAsync(sessao.Id, Usuario, null, Lote(Progresso(0, 60)));
        await coletor.RecordAsync(sessao.Id, Usuario, null, Lote(Progresso(0, 60)));

        await using var leitura = postgres.CreateContext();
        Assert.Equal(60, (await leitura.PlaybackSessions.SingleAsync()).WatchedSeconds);
    }

    [Fact]
    public async Task Pular_um_trecho_deixa_a_lacuna_registrada()
    {
        var video = await CriarVideoAsync();
        var (coletor, db) = Criar();
        await using var _ = db;
        var sessao = await coletor.StartAsync(video.Id, Usuario, null, null, null, null, null);

        await coletor.RecordAsync(sessao.Id, Usuario, null, Lote(
            Progresso(0, 60),
            new PlaybackEventReport("seek", 480),
            Progresso(480, 540)));

        await using var leitura = postgres.CreateContext();
        var gravada = await leitura.PlaybackSessions.Include(s => s.Intervals).SingleAsync();

        Assert.Equal(2, gravada.Intervals.Count);
        Assert.Equal(120, gravada.WatchedSeconds);
        Assert.Equal(540, gravada.FurthestPosition);
    }

    [Fact]
    public async Task Chegar_ao_fim_marca_a_sessao_como_concluida()
    {
        var video = await CriarVideoAsync();
        var (coletor, db) = Criar();
        await using var _ = db;
        var sessao = await coletor.StartAsync(video.Id, Usuario, null, null, null, null, null);

        await coletor.RecordAsync(sessao.Id, Usuario, null, Lote(
            Progresso(0, Duracao), new PlaybackEventReport("ended", Duracao)));

        await using var leitura = postgres.CreateContext();
        Assert.True((await leitura.PlaybackSessions.SingleAsync()).Completed);
    }

    [Fact]
    public async Task O_trecho_relatado_nao_passa_da_duracao_do_video()
    {
        var video = await CriarVideoAsync();
        var (coletor, db) = Criar();
        await using var _ = db;
        var sessao = await coletor.StartAsync(video.Id, Usuario, null, null, null, null, null);

        await coletor.RecordAsync(sessao.Id, Usuario, null, Lote(Progresso(0, 99999)));

        await using var leitura = postgres.CreateContext();
        Assert.Equal(Duracao, (await leitura.PlaybackSessions.SingleAsync()).WatchedSeconds);
    }

    [Fact]
    public async Task Guarda_a_maior_qualidade_e_conta_os_erros()
    {
        var video = await CriarVideoAsync();
        var (coletor, db) = Criar();
        await using var _ = db;
        var sessao = await coletor.StartAsync(video.Id, Usuario, null, null, null, null, null);

        await coletor.RecordAsync(sessao.Id, Usuario, null, Lote(
            new PlaybackEventReport("quality", 10, Detalhe: "480p"),
            new PlaybackEventReport("quality", 20, Detalhe: "1080p"),
            new PlaybackEventReport("error", 30, Detalhe: "bufferStalledError")));

        await using var leitura = postgres.CreateContext();
        var gravada = await leitura.PlaybackSessions.SingleAsync();

        Assert.Equal("1080p", gravada.MaxQuality);
        Assert.Equal(1, gravada.ErrorCount);
    }

    [Fact]
    public async Task Guarda_o_evento_bruto_de_cada_relato()
    {
        var video = await CriarVideoAsync();
        var (coletor, db) = Criar();
        await using var _ = db;
        var sessao = await coletor.StartAsync(video.Id, Usuario, null, null, null, null, null);

        await coletor.RecordAsync(sessao.Id, Usuario, null, Lote(
            Progresso(0, 30), new PlaybackEventReport("pause", 30)));

        await using var leitura = postgres.CreateContext();

        Assert.Equal(3, await leitura.PlaybackEvents.CountAsync());
        Assert.Equal(1, await leitura.PlaybackEvents.CountAsync(e => e.Type == PlaybackEventType.Pause));
    }

    [Fact]
    public async Task A_sessao_de_outra_pessoa_nao_aceita_relatos()
    {
        var video = await CriarVideoAsync();
        var (coletor, db) = Criar();
        await using var _ = db;
        var sessao = await coletor.StartAsync(video.Id, Usuario, null, null, null, null, null);

        // Adivinhar um identificador não pode permitir poluir o histórico de outra pessoa.
        var aceito = await coletor.RecordAsync(sessao.Id, Guid.CreateVersion7(), null, Lote(Progresso(0, 600)));

        Assert.False(aceito);

        await using var leitura = postgres.CreateContext();
        Assert.Equal(0, (await leitura.PlaybackSessions.SingleAsync()).WatchedSeconds);
    }

    [Fact]
    public async Task A_sessao_anonima_e_identificada_pelo_navegador()
    {
        var video = await CriarVideoAsync();
        var (coletor, db) = Criar();
        await using var _ = db;
        var sessao = await coletor.StartAsync(video.Id, null, "visitante-1", null, null, null, null);

        Assert.True(await coletor.RecordAsync(sessao.Id, null, "visitante-1", Lote(Progresso(0, 30))));
        Assert.False(await coletor.RecordAsync(sessao.Id, null, "visitante-2", Lote(Progresso(30, 60))));

        await using var leitura = postgres.CreateContext();
        Assert.Equal(30, (await leitura.PlaybackSessions.SingleAsync()).WatchedSeconds);
    }

    [Fact]
    public async Task Sessao_inexistente_nao_aceita_relatos()
    {
        var (coletor, db) = Criar();
        await using var _ = db;

        Assert.False(await coletor.RecordAsync(Guid.CreateVersion7(), Usuario, null, Lote(Progresso(0, 30))));
    }

    [Fact]
    public async Task Encerra_a_sessao_quando_a_pagina_e_fechada()
    {
        var video = await CriarVideoAsync();
        var (coletor, db) = Criar();
        await using var _ = db;
        var sessao = await coletor.StartAsync(video.Id, Usuario, null, null, null, null, null);

        _relogio.Advance(TimeSpan.FromMinutes(3));
        await coletor.EndAsync(sessao.Id, Usuario, null);

        await using var leitura = postgres.CreateContext();
        Assert.Equal(Agora.AddMinutes(3), (await leitura.PlaybackSessions.SingleAsync()).EndedAt);
    }

    [Fact]
    public async Task Sessao_sem_sinal_e_fechada_pela_inatividade()
    {
        var video = await CriarVideoAsync();
        var (coletor, db) = Criar();
        await using var _ = db;
        var sessao = await coletor.StartAsync(video.Id, Usuario, null, null, null, null, null);
        await coletor.RecordAsync(sessao.Id, Usuario, null, Lote(Progresso(0, 30)));

        _relogio.Advance(AnalyticsCollector.InactivityTimeout + TimeSpan.FromMinutes(1));

        Assert.Equal(1, await coletor.CloseStaleAsync());

        await using var leitura = postgres.CreateContext();
        var gravada = await leitura.PlaybackSessions.SingleAsync();

        // O fim registrado é o último sinal, e não o momento da varredura.
        Assert.Equal(Agora, gravada.EndedAt);
    }

    [Fact]
    public async Task Sessao_ativa_nao_e_fechada_pela_varredura()
    {
        var video = await CriarVideoAsync();
        var (coletor, db) = Criar();
        await using var _ = db;
        await coletor.StartAsync(video.Id, Usuario, null, null, null, null, null);

        _relogio.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(0, await coletor.CloseStaleAsync());
    }

    [Fact]
    public async Task Garante_a_particao_do_mes_e_do_seguinte()
    {
        var (coletor, db) = Criar();
        await using var _ = db;

        await coletor.EnsurePartitionsAsync(new DateTimeOffset(2027, 5, 10, 0, 0, 0, TimeSpan.Zero));

        await using var leitura = postgres.CreateContext();
        var particoes = await leitura.Database
            .SqlQuery<string>($"SELECT relname AS \"Value\" FROM pg_class WHERE relname LIKE 'playback_events_2027%'")
            .ToListAsync();

        Assert.Contains("playback_events_2027_05", particoes);
        Assert.Contains("playback_events_2027_06", particoes);
    }

    [Fact]
    public async Task O_evento_cai_na_particao_do_proprio_mes()
    {
        var video = await CriarVideoAsync();
        var (coletor, db) = Criar();
        await using var _ = db;

        await coletor.StartAsync(video.Id, Usuario, null, null, null, null, null);

        await using var leitura = postgres.CreateContext();
        var particao = await leitura.Database
            .SqlQuery<string>($"SELECT tableoid::regclass::text AS \"Value\" FROM playback_events LIMIT 1")
            .SingleAsync();

        Assert.Equal("playback_events_2026_09", particao);
    }
}
