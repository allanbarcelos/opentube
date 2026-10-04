// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Captions;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Queue;
using OpenTube.Infrastructure.Storage;
using OpenTube.TestSupport;
using OpenTube.Worker.Jobs;
using OpenTube.Worker.Media;
using OpenTube.Worker.Tests.Support;

namespace OpenTube.Worker.Tests.Jobs;

/// <summary>
/// Geração de legenda a partir da fala, com o texto alimentando a busca.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class TranscriptionJobHandlerTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();

    private readonly FakeTimeProvider _relogio = new(Agora);
    private readonly FakeTranscriber _transcritor = new();

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TranscriptionJobHandler Criar(OpenTube.Infrastructure.Persistence.OpenTubeDbContext db, IVideoStorage storage) =>
        new(db, storage, storage, _transcritor, Microsoft.Extensions.Options.Options.Create(new TranscriptionOptions()),
            _relogio, NullLogger<TranscriptionJobHandler>.Instance);

    private async Task<Video> PrepararVideoAsync(IVideoStorage storage)
    {
        var videoId = Guid.CreateVersion7();
        var chave = StorageKeys.Original(videoId, "amostra.mp4");

        await storage.PutTextAsync(StorageBucket.Originals, chave, "arquivo-falso", MediaTypes.Mp4);

        await using var db = postgres.CreateContext();

        var video = Video.CreateDraft("Reunião", $"v-{videoId:n}"[..20], chave, Admin, Agora, id: videoId);
        video.MarkUploaded(1024);
        video.StartProcessing();
        video.MarkReady(StorageKeys.VodPrefix(videoId), 600, 1280, 720, null, null, Agora);

        db.Videos.Add(video);
        await db.SaveChangesAsync();

        return video;
    }

    /// <summary>Pedido como a aplicação faz: a legenda já existe, em processamento.</summary>
    private async Task<VideoAsset> PedirAsync(Video video, string idioma = "pt-br")
    {
        await using var db = postgres.CreateContext();
        var legenda = VideoAsset.CaptionTranscriptionRequest(video.Id, idioma, null, StorageKeys.Caption(video.Id, idioma), Agora);
        db.VideoAssets.Add(legenda);
        await db.SaveChangesAsync();

        return legenda;
    }

    private static QueuedJob Job(Video video, VideoAsset legenda, int tentativa = 1) =>
        new(Guid.CreateVersion7(), JobKind.Transcript, video.Id,
            System.Text.Json.JsonSerializer.Serialize(
                new TranscriptionPayload(video.Id, video.OriginalKey, legenda.Language, legenda.Id)), tentativa);

    private async Task<VideoAsset> LegendaAsync(Guid id)
    {
        await using var db = postgres.CreateContext();
        return await db.VideoAssets.SingleAsync(a => a.Id == id);
    }

    [Fact]
    public async Task Gera_a_legenda_no_idioma_pedido_e_marca_pronta()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);
        var pedido = await PedirAsync(video, "en");

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video, pedido));

        // A ferramenta recebe só o idioma, sem região: é o que o Whisper entende.
        Assert.Equal("en", _transcritor.IdiomaPedido);

        var legenda = await LegendaAsync(pedido.Id);
        Assert.Equal(CaptionStatus.Ready, legenda.Status);
        Assert.Equal(CaptionSource.Automatic, legenda.Source);
        Assert.True(legenda.HasContent);

        var conteudo = await storage.GetTextAsync(StorageBucket.Vod, legenda.StorageKey);
        Assert.Equal("Bom dia a todos.", CaptionDocument.Parse(conteudo).Cues.Single().Text);
    }

    [Fact]
    public async Task Regiao_do_idioma_fica_so_na_legenda()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);
        var pedido = await PedirAsync(video, "pt-br");

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video, pedido));

        Assert.Equal("pt", _transcritor.IdiomaPedido);
        Assert.Equal("pt-br", (await LegendaAsync(pedido.Id)).Language);
    }

    [Fact]
    public async Task A_fala_transcrita_passa_a_ser_encontrada_na_busca()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video, await PedirAsync(video)));

        await using var leitura = postgres.CreateContext();
        Assert.Equal("Bom dia a todos.", (await leitura.Videos.SingleAsync()).Transcript);
    }

    [Fact]
    public async Task O_resultado_da_ferramenta_e_guardado_normalizado()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);
        var pedido = await PedirAsync(video);

        // Fora de ordem e com tempo sem horas: sai ordenado e no formato completo.
        _transcritor.Vtt = "WEBVTT\n\n00:05.000 --> 00:06.000\nDepois\n\n00:01.000 --> 00:02.000\nAntes\n";

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video, pedido));

        var conteudo = await storage.GetTextAsync(StorageBucket.Vod, (await LegendaAsync(pedido.Id)).StorageKey);
        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nAntes\n\n00:00:05.000 --> 00:00:06.000\nDepois\n", conteudo);
    }

    [Fact]
    public async Task Transcrever_de_novo_substitui_o_conteudo_sem_criar_outra_legenda()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);
        var pedido = await PedirAsync(video);

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video, pedido));

        await using (var db = postgres.CreateContext())
        {
            var legenda = await db.VideoAssets.SingleAsync();
            legenda.StartTranscription(Agora);
            await db.SaveChangesAsync();
        }

        _transcritor.Vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:04.000\nTexto corrigido.\n";

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video, pedido));

        await using var leitura = postgres.CreateContext();
        Assert.Equal(1, await leitura.VideoAssets.CountAsync(a => a.Kind == VideoAssetKind.Caption));
        Assert.Equal("Texto corrigido.", (await leitura.Videos.SingleAsync()).Transcript);
    }

    [Fact]
    public async Task Falha_antes_da_ultima_tentativa_mantem_processando()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);
        var pedido = await PedirAsync(video);
        _transcritor.Falha = new InvalidOperationException("modelo não encontrado");

        await using (var db = postgres.CreateContext())
            await Assert.ThrowsAsync<InvalidOperationException>(() => Criar(db, storage).HandleAsync(Job(video, pedido, tentativa: 1)));

        Assert.Equal(CaptionStatus.Processing, (await LegendaAsync(pedido.Id)).Status);
    }

    [Fact]
    public async Task Falha_na_ultima_tentativa_registra_o_motivo()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);
        var pedido = await PedirAsync(video);
        _transcritor.Falha = new InvalidOperationException("modelo não encontrado");

        await using (var db = postgres.CreateContext())
        {
            var erro = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Criar(db, storage).HandleAsync(Job(video, pedido, tentativa: ProcessingJob.MaxAttempts)));

            Assert.Contains("modelo não encontrado", erro.Message);
        }

        var legenda = await LegendaAsync(pedido.Id);
        Assert.Equal(CaptionStatus.Failed, legenda.Status);
        Assert.Equal("modelo não encontrado", legenda.Error);
    }

    [Fact]
    public async Task Sem_a_ferramenta_configurada_a_falha_e_registrada_de_uma_vez()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);
        var pedido = await PedirAsync(video);
        _transcritor.IsAvailable = false;

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video, pedido));

        Assert.Equal(0, _transcritor.Chamadas);

        var legenda = await LegendaAsync(pedido.Id);
        Assert.Equal(CaptionStatus.Failed, legenda.Status);
        Assert.Equal("Automatic transcription is not configured on this server.", legenda.Error);
    }

    [Fact]
    public async Task Legenda_removida_durante_a_espera_descarta_o_resultado()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);
        var pedido = await PedirAsync(video);

        await using (var db = postgres.CreateContext())
        {
            db.VideoAssets.Remove(await db.VideoAssets.SingleAsync());
            await db.SaveChangesAsync();
        }

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video, pedido));

        Assert.Equal(0, _transcritor.Chamadas);

        await using var leitura = postgres.CreateContext();
        Assert.Empty(await leitura.VideoAssets.ToListAsync());
    }

    [Fact]
    public async Task Video_excluido_nao_e_transcrito()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);
        var pedido = await PedirAsync(video);

        await using (var db = postgres.CreateContext())
        {
            var alvo = await db.Videos.SingleAsync(v => v.Id == video.Id);
            alvo.SoftDelete(Agora);
            await db.SaveChangesAsync();
        }

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video, pedido));

        Assert.Equal(0, _transcritor.Chamadas);
        Assert.Equal(CaptionStatus.Failed, (await LegendaAsync(pedido.Id)).Status);
    }

    [Fact]
    public async Task Trabalho_antigo_sem_legenda_no_pedido_ainda_e_atendido()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);

        var antigo = new QueuedJob(Guid.CreateVersion7(), JobKind.Transcript, video.Id,
            System.Text.Json.JsonSerializer.Serialize(new { video.Id, VideoId = video.Id, video.OriginalKey }), 1);

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(antigo);

        await using var leitura = postgres.CreateContext();
        var legenda = await leitura.VideoAssets.SingleAsync();

        Assert.Equal("pt", legenda.Language);
        Assert.Equal(CaptionStatus.Ready, legenda.Status);
    }

    [Fact]
    public async Task Recusa_trabalho_sem_parametros()
    {
        using var storage = minio.CreateStorage();
        await using var db = postgres.CreateContext();

        var job = new QueuedJob(Guid.CreateVersion7(), JobKind.Transcript, null, "{}", 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Criar(db, storage).HandleAsync(job));
    }

    /// <summary>Legenda já existente no idioma, como se tivesse sido enviada ou gerada antes.</summary>
    private async Task<VideoAsset> LegendaProntaAsync(IVideoStorage storage, Video video, string idioma, CaptionSource origem)
    {
        var chave = StorageKeys.Caption(video.Id, idioma);
        await storage.PutTextAsync(StorageBucket.Vod, chave, "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nTexto antigo.\n", MediaTypes.WebVtt);

        await using var db = postgres.CreateContext();
        var legenda = VideoAsset.CaptionWithContent(video.Id, idioma, null, chave, origem, 60, Agora);
        db.VideoAssets.Add(legenda);
        await db.SaveChangesAsync();

        return legenda;
    }

    [Fact]
    public async Task Deteccao_automatica_da_a_legenda_o_idioma_falado()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);
        var pedido = await PedirAsync(video, "auto");
        _transcritor.IdiomaDetectado = "en";

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video, pedido));

        Assert.Equal("auto", _transcritor.IdiomaPedido);

        var legenda = await LegendaAsync(pedido.Id);
        Assert.Equal("en", legenda.Language);
        Assert.Equal(StorageKeys.Caption(video.Id, "en"), legenda.StorageKey);
        Assert.Equal(CaptionLanguage.DisplayName("en"), legenda.Label);
        Assert.Equal(CaptionStatus.Ready, legenda.Status);

        var conteudo = await storage.GetTextAsync(StorageBucket.Vod, legenda.StorageKey);
        Assert.Equal("Bom dia a todos.", CaptionDocument.Parse(conteudo).Cues.Single().Text);
    }

    [Fact]
    public async Task Deteccao_automatica_atualiza_a_legenda_automatica_ja_existente_do_idioma()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);
        var anterior = await LegendaProntaAsync(storage, video, "pt", CaptionSource.Automatic);
        var pedido = await PedirAsync(video, "auto");

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video, pedido));

        await using var leitura = postgres.CreateContext();
        var legenda = await leitura.VideoAssets.SingleAsync();

        // A provisória sai; a do idioma recebe o resultado.
        Assert.Equal(anterior.Id, legenda.Id);
        Assert.Equal(CaptionStatus.Ready, legenda.Status);

        var conteudo = await storage.GetTextAsync(StorageBucket.Vod, legenda.StorageKey);
        Assert.Equal("Bom dia a todos.", CaptionDocument.Parse(conteudo).Cues.Single().Text);
    }

    [Fact]
    public async Task Deteccao_automatica_nunca_sobrescreve_legenda_corrigida_a_mao()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);
        var corrigida = await LegendaProntaAsync(storage, video, "pt", CaptionSource.Edited);
        var pedido = await PedirAsync(video, "auto");

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video, pedido));

        var provisoria = await LegendaAsync(pedido.Id);
        Assert.Equal(CaptionStatus.Failed, provisoria.Status);
        Assert.Equal("The detected language already has a caption. Delete it or use Regenerate on it.", provisoria.Error);

        var conteudo = await storage.GetTextAsync(StorageBucket.Vod, corrigida.StorageKey);
        Assert.Equal("Texto antigo.", CaptionDocument.Parse(conteudo).Cues.Single().Text);
        Assert.Equal(CaptionSource.Edited, (await LegendaAsync(corrigida.Id)).Source);
    }
}
