// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Analytics;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Storage;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.Shared.Analytics;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Analytics;

[Collection(IntegrationCollection.Name)]
public class AnalyticsAggregatorTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const double Duracao = 600;

    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Hoje = new(2026, 9, 24);
    private static readonly Guid Admin = Guid.CreateVersion7();

    private readonly FakeTimeProvider _relogio = new(Agora);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private AnalyticsCollector Coletor(OpenTubeDbContext db) =>
        new(db, _relogio, NullLogger<AnalyticsCollector>.Instance);

    private AnalyticsAggregator Agregador(OpenTubeDbContext db) =>
        new(db, _relogio, NullLogger<AnalyticsAggregator>.Instance);

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

    private async Task<Guid> AssistirAsync(Guid videoId, Guid? usuario, string? anonimo, double de, double ate, bool concluiu = false)
    {
        await using var db = postgres.CreateContext();
        var coletor = Coletor(db);

        var sessao = await coletor.StartAsync(videoId, usuario, anonimo, null, null, null, null);

        var eventos = new List<PlaybackEventReport> { new("progress", ate, de, ate) };
        if (concluiu)
            eventos.Add(new PlaybackEventReport("ended", ate));

        await coletor.RecordAsync(sessao.Id, usuario, anonimo, new PlaybackBatch([.. eventos]));

        return sessao.Id;
    }

    private async Task<Guid> CriarUsuarioAsync(string email)
    {
        await using var db = postgres.CreateContext();
        var usuario = User.Create(EmailAddress.Parse(email), Agora);

        db.Users.Add(usuario);
        await db.SaveChangesAsync();

        return usuario.Id;
    }

    [Fact]
    public async Task Consolida_os_numeros_do_dia()
    {
        var video = await CriarVideoAsync();
        var pessoa = await CriarUsuarioAsync("a@barcelos.dev");

        await AssistirAsync(video.Id, pessoa, null, 0, 300);
        await AssistirAsync(video.Id, null, "visitante-1", 0, Duracao, concluiu: true);

        await using var db = postgres.CreateContext();
        await Agregador(db).RollupDayAsync(Hoje);

        await using var leitura = postgres.CreateContext();
        var estatistica = await leitura.VideoDailyStats.SingleAsync();

        Assert.Equal(2, estatistica.Views);
        Assert.Equal(2, estatistica.UniqueViewers);
        Assert.Equal(900, estatistica.WatchSeconds);
        Assert.Equal(1, estatistica.Completions);
    }

    [Fact]
    public async Task A_mesma_pessoa_em_duas_sessoes_conta_uma_vez_como_espectador()
    {
        var video = await CriarVideoAsync();
        var pessoa = await CriarUsuarioAsync("a@barcelos.dev");

        await AssistirAsync(video.Id, pessoa, null, 0, 100);
        await AssistirAsync(video.Id, pessoa, null, 100, 200);

        await using var db = postgres.CreateContext();
        await Agregador(db).RollupDayAsync(Hoje);

        await using var leitura = postgres.CreateContext();
        var estatistica = await leitura.VideoDailyStats.SingleAsync();

        Assert.Equal(2, estatistica.Views);
        Assert.Equal(1, estatistica.UniqueViewers);
    }

    [Fact]
    public async Task Reagregar_o_mesmo_dia_nao_duplica_os_numeros()
    {
        var video = await CriarVideoAsync();
        await AssistirAsync(video.Id, null, "visitante-1", 0, 300);

        await using var db = postgres.CreateContext();
        await Agregador(db).RollupDayAsync(Hoje);
        await Agregador(db).RollupDayAsync(Hoje);

        await using var leitura = postgres.CreateContext();
        var estatistica = await leitura.VideoDailyStats.SingleAsync();

        Assert.Equal(1, estatistica.Views);
        Assert.Equal(300, estatistica.WatchSeconds);
    }

    [Fact]
    public async Task Dia_que_perdeu_todas_as_sessoes_e_descartado()
    {
        var video = await CriarVideoAsync();
        var sessao = await AssistirAsync(video.Id, null, "visitante-1", 0, 300);

        await using (var db = postgres.CreateContext())
            await Agregador(db).RollupDayAsync(Hoje);

        await using (var db = postgres.CreateContext())
        {
            db.PlaybackSessions.Remove(await db.PlaybackSessions.SingleAsync(s => s.Id == sessao));
            await db.SaveChangesAsync();
        }

        await using (var db = postgres.CreateContext())
            await Agregador(db).RollupDayAsync(Hoje);

        await using var leitura = postgres.CreateContext();
        Assert.Empty(await leitura.VideoDailyStats.ToListAsync());
    }

    [Fact]
    public async Task Monta_a_curva_de_retencao_do_video()
    {
        var video = await CriarVideoAsync();

        await AssistirAsync(video.Id, null, "visitante-1", 0, Duracao);
        await AssistirAsync(video.Id, null, "visitante-2", 0, 300);
        await AssistirAsync(video.Id, null, "visitante-3", 0, 60);

        await using var db = postgres.CreateContext();
        await Agregador(db).RebuildRetentionAsync(video.Id);

        await using var leitura = postgres.CreateContext();
        var fatias = await leitura.VideoRetentionBuckets
            .Where(b => b.VideoId == video.Id)
            .OrderBy(b => b.BucketIndex)
            .Select(b => b.Viewers)
            .ToListAsync();

        Assert.Equal(100, fatias.Count);
        Assert.Equal(3, fatias[0]);
        Assert.Equal(2, fatias[40]);
        Assert.Equal(1, fatias[99]);
    }

    [Fact]
    public async Task A_mesma_pessoa_nao_aparece_duas_vezes_na_curva()
    {
        var video = await CriarVideoAsync();
        var pessoa = await CriarUsuarioAsync("a@barcelos.dev");

        await AssistirAsync(video.Id, pessoa, null, 0, Duracao);
        await AssistirAsync(video.Id, pessoa, null, 0, Duracao);

        await using var db = postgres.CreateContext();
        await Agregador(db).RebuildRetentionAsync(video.Id);

        await using var leitura = postgres.CreateContext();
        Assert.Equal(1, (await leitura.VideoRetentionBuckets.OrderBy(b => b.BucketIndex).FirstAsync()).Viewers);
    }

    [Fact]
    public async Task Refazer_a_curva_atualiza_os_valores_em_vez_de_acumular()
    {
        var video = await CriarVideoAsync();
        await AssistirAsync(video.Id, null, "visitante-1", 0, Duracao);

        await using var db = postgres.CreateContext();
        await Agregador(db).RebuildRetentionAsync(video.Id);
        await AssistirAsync(video.Id, null, "visitante-2", 0, Duracao);
        await Agregador(db).RebuildRetentionAsync(video.Id);

        await using var leitura = postgres.CreateContext();

        Assert.Equal(100, await leitura.VideoRetentionBuckets.CountAsync());
        Assert.Equal(2, (await leitura.VideoRetentionBuckets.OrderBy(b => b.BucketIndex).FirstAsync()).Viewers);
    }

    [Fact]
    public async Task Video_sem_duracao_conhecida_nao_gera_curva()
    {
        await using var db = postgres.CreateContext();
        var videoId = Guid.CreateVersion7();

        db.Videos.Add(Video.CreateDraft("Rascunho", "rascunho", "chave", Admin, Agora, id: videoId));
        await db.SaveChangesAsync();

        await Agregador(db).RebuildRetentionAsync(videoId);

        await using var leitura = postgres.CreateContext();
        Assert.Empty(await leitura.VideoRetentionBuckets.ToListAsync());
    }

    [Fact]
    public async Task A_passagem_periodica_cobre_o_dia_e_a_curva()
    {
        var video = await CriarVideoAsync();
        await AssistirAsync(video.Id, null, "visitante-1", 0, Duracao, concluiu: true);

        await using var db = postgres.CreateContext();
        var resultado = await Agregador(db).RollupRecentAsync();

        Assert.Equal(1, resultado.Videos);

        await using var leitura = postgres.CreateContext();
        Assert.Single(await leitura.VideoDailyStats.ToListAsync());
        Assert.Equal(100, await leitura.VideoRetentionBuckets.CountAsync());
    }

    [Fact]
    public async Task Descarta_evento_bruto_vencido_preservando_os_agregados()
    {
        var video = await CriarVideoAsync();
        await AssistirAsync(video.Id, null, "visitante-1", 0, 300);

        await using var db = postgres.CreateContext();
        await Agregador(db).RollupDayAsync(Hoje);

        _relogio.Advance(TimeSpan.FromDays(400));

        var descartados = await Agregador(db).PruneEventsAsync(TimeSpan.FromDays(365));

        Assert.True(descartados > 0);

        await using var leitura = postgres.CreateContext();
        Assert.Empty(await leitura.PlaybackEvents.ToListAsync());
        // O que se perde é a investigação pontual, não o histórico de audiência.
        Assert.Single(await leitura.VideoDailyStats.ToListAsync());
    }

    [Fact]
    public async Task Evento_dentro_do_prazo_nao_e_descartado()
    {
        var video = await CriarVideoAsync();
        await AssistirAsync(video.Id, null, "visitante-1", 0, 300);

        await using var db = postgres.CreateContext();

        Assert.Equal(0, await Agregador(db).PruneEventsAsync(TimeSpan.FromDays(365)));
    }

    [Fact]
    public async Task Recusa_prazo_de_retencao_invalido()
    {
        await using var db = postgres.CreateContext();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Agregador(db).PruneEventsAsync(TimeSpan.Zero));
    }
}
