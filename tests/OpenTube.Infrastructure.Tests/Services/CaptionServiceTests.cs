using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Captions;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Queue;
using OpenTube.Infrastructure.Services;
using OpenTube.Infrastructure.Storage;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Services;

/// <summary>
/// Legendas: uma por idioma, pedidas à transcrição sem duplicar, enviadas ou editadas.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class CaptionServiceTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();

    private const string Vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nBom dia a todos.\n";

    private readonly FakeTimeProvider _relogio = new(Agora);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private (CaptionService Servico, OpenTubeDbContext Db, S3VideoStorage Storage) Criar()
    {
        var db = postgres.CreateContext();
        var storage = minio.CreateStorage();

        return (new CaptionService(db, storage, new PostgresJobQueue(db, _relogio), _relogio, NullLogger<CaptionService>.Instance), db, storage);
    }

    private async Task<Video> CriarVideoAsync()
    {
        var videoId = Guid.CreateVersion7();
        var chave = StorageKeys.Original(videoId, "amostra.mp4");

        using (var storage = minio.CreateStorage())
            await storage.PutTextAsync(StorageBucket.Originals, chave, "arquivo-falso", MediaTypes.Mp4);

        await using var db = postgres.CreateContext();
        var video = Video.CreateDraft("Reunião", $"reuniao-{videoId:n}"[..30], chave, Admin, Agora, id: videoId);
        video.MarkUploaded(2048);
        video.StartProcessing();
        video.MarkReady(StorageKeys.VodPrefix(videoId), 120, 1280, 720, null, null, Agora);
        db.Videos.Add(video);
        await db.SaveChangesAsync();

        return video;
    }

    [Fact]
    public async Task Pedir_a_transcricao_cria_a_legenda_processando_e_poe_na_fila()
    {
        var video = await CriarVideoAsync();
        var (servico, db, storage) = Criar();
        await using var _ = db;
        using var __ = storage;

        var id = await servico.RequestTranscriptionAsync(video.Id, "pt-BR");

        await using var leitura = postgres.CreateContext();
        var legenda = await leitura.VideoAssets.SingleAsync();
        var trabalho = await leitura.ProcessingJobs.SingleAsync();

        Assert.Equal(id, legenda.Id);
        Assert.Equal("pt-br", legenda.Language);
        Assert.Equal("Português (Brasil)", legenda.Label);
        Assert.Equal(CaptionStatus.Processing, legenda.Status);
        Assert.False(legenda.HasContent);
        Assert.Equal(JobKind.Transcript, trabalho.Kind);
        using var pedido = System.Text.Json.JsonDocument.Parse(trabalho.Payload);
        Assert.Equal("pt-br", pedido.RootElement.GetProperty("language").GetString());
        Assert.Equal(id, pedido.RootElement.GetProperty("assetId").GetGuid());
    }

    [Fact]
    public async Task Idioma_em_processamento_nao_pode_ser_pedido_de_novo()
    {
        var video = await CriarVideoAsync();
        var (servico, db, storage) = Criar();
        await using var _ = db;
        using var __ = storage;

        await servico.RequestTranscriptionAsync(video.Id, "pt-br");

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() => servico.RequestTranscriptionAsync(video.Id, "PT-BR"));
        Assert.Equal("A transcription for this language is already in progress.", erro.Message);

        await using var leitura = postgres.CreateContext();
        Assert.Equal(1, await leitura.ProcessingJobs.CountAsync());
    }

    [Fact]
    public async Task Outro_idioma_pode_ser_pedido_ao_mesmo_tempo()
    {
        var video = await CriarVideoAsync();
        var (servico, db, storage) = Criar();
        await using var _ = db;
        using var __ = storage;

        await servico.RequestTranscriptionAsync(video.Id, "pt-br");
        await servico.RequestTranscriptionAsync(video.Id, "en");

        await using var leitura = postgres.CreateContext();
        Assert.Equal(2, await leitura.VideoAssets.CountAsync(a => a.Status == CaptionStatus.Processing));
        Assert.Equal(2, await leitura.ProcessingJobs.CountAsync());
    }

    [Fact]
    public async Task Pedidos_simultaneos_do_mesmo_idioma_geram_um_so_trabalho()
    {
        var video = await CriarVideoAsync();

        // Cada pedido num contexto próprio, como requisições distintas chegando juntas.
        var resultados = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            var (servico, db, storage) = Criar();
            await using var __ = db;
            using var ___ = storage;

            try
            {
                await servico.RequestTranscriptionAsync(video.Id, "pt-br");
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }));

        Assert.Equal(1, resultados.Count(r => r));

        await using var leitura = postgres.CreateContext();
        Assert.Equal(1, await leitura.VideoAssets.CountAsync());
        Assert.Equal(1, await leitura.ProcessingJobs.CountAsync());
    }

    [Fact]
    public async Task Regerar_uma_legenda_pronta_mantem_o_conteudo_ate_terminar()
    {
        var video = await CriarVideoAsync();
        var (servico, db, storage) = Criar();
        await using var _ = db;
        using var __ = storage;

        var enviada = await servico.UploadAsync(video.Id, "pt-br", "Português", Vtt);
        await servico.RequestTranscriptionAsync(video.Id, "pt-br");

        await using var leitura = postgres.CreateContext();
        var legenda = await leitura.VideoAssets.SingleAsync();

        Assert.Equal(enviada.Id, legenda.Id);
        Assert.Equal(CaptionStatus.Processing, legenda.Status);
        Assert.True(legenda.HasContent);
        Assert.NotNull(await Criar().Servico.ReadAsync(legenda.Id));
    }

    [Fact]
    public async Task Envio_e_edicao_sao_recusados_durante_a_transcricao()
    {
        var video = await CriarVideoAsync();
        var (servico, db, storage) = Criar();
        await using var _ = db;
        using var __ = storage;

        var id = await servico.RequestTranscriptionAsync(video.Id, "pt-br");

        var envio = await Assert.ThrowsAsync<InvalidOperationException>(() => servico.UploadAsync(video.Id, "pt-br", null, Vtt));
        var edicao = await Assert.ThrowsAsync<InvalidOperationException>(() => servico.SaveEditAsync(video.Id, id, Vtt));

        Assert.Equal("Wait for the transcription of this language to finish.", envio.Message);
        Assert.Equal("Wait for the transcription of this language to finish.", edicao.Message);
    }

    [Fact]
    public async Task Envio_de_srt_e_guardado_como_webvtt()
    {
        var video = await CriarVideoAsync();
        var (servico, db, storage) = Criar();
        await using var _ = db;
        using var __ = storage;

        var legenda = await servico.UploadAsync(video.Id, "en", null, "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello\r\n");

        var conteudo = await storage.GetTextAsync(StorageBucket.Vod, legenda.StorageKey);
        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHello\n", conteudo);
        Assert.Equal(CaptionSource.Upload, legenda.Source);
        Assert.Equal("English", legenda.Label);
    }

    [Fact]
    public async Task Envio_para_idioma_existente_substitui_o_conteudo()
    {
        var video = await CriarVideoAsync();
        var (servico, db, storage) = Criar();
        await using var _ = db;
        using var __ = storage;

        var primeira = await servico.UploadAsync(video.Id, "pt-br", null, Vtt);
        var segunda = await servico.UploadAsync(video.Id, "pt-BR", null, "WEBVTT\n\n00:00:05.000 --> 00:00:06.000\nOutro texto.\n");

        Assert.Equal(primeira.Id, segunda.Id);

        await using var leitura = postgres.CreateContext();
        Assert.Equal(1, await leitura.VideoAssets.CountAsync());
        Assert.Contains("Outro texto.", await storage.GetTextAsync(StorageBucket.Vod, primeira.StorageKey));
    }

    [Fact]
    public async Task Edicao_grava_normalizado_marca_a_origem_e_atualiza_a_busca()
    {
        var video = await CriarVideoAsync();
        var (servico, db, storage) = Criar();
        await using var _ = db;
        using var __ = storage;

        var legenda = await servico.UploadAsync(video.Id, "pt-br", null, Vtt);

        await servico.SaveEditAsync(video.Id, legenda.Id,
            "WEBVTT\n\n00:04.000 --> 00:05.000\nSegunda fala corrigida\n\n00:01.000 --> 00:02.000\nPrimeira\n");

        await using var leitura = postgres.CreateContext();
        var editada = await leitura.VideoAssets.SingleAsync();
        var conteudo = await storage.GetTextAsync(StorageBucket.Vod, editada.StorageKey);

        Assert.Equal(CaptionSource.Edited, editada.Source);
        Assert.Equal(["Primeira", "Segunda fala corrigida"], CaptionDocument.Parse(conteudo).Cues.Select(c => c.Text));
        Assert.Equal("Primeira Segunda fala corrigida", (await leitura.Videos.SingleAsync()).Transcript);
    }

    [Fact]
    public async Task Edicao_invalida_nao_altera_nada()
    {
        var video = await CriarVideoAsync();
        var (servico, db, storage) = Criar();
        await using var _ = db;
        using var __ = storage;

        var legenda = await servico.UploadAsync(video.Id, "pt-br", null, Vtt);

        var erro = await Assert.ThrowsAsync<CaptionFormatException>(() =>
            servico.SaveEditAsync(video.Id, legenda.Id, "WEBVTT\n\n00:00:05.000 --> 00:00:04.000\nAo contrário\n"));

        Assert.Equal("Cue {0}: the end time must come after the start time.", erro.Key);
        Assert.Contains("Bom dia a todos.", await storage.GetTextAsync(StorageBucket.Vod, legenda.StorageKey));
    }

    [Fact]
    public async Task Legenda_sem_conteudo_nao_vai_para_o_player()
    {
        var video = await CriarVideoAsync();
        var (servico, db, storage) = Criar();
        await using var _ = db;
        using var __ = storage;

        await servico.RequestTranscriptionAsync(video.Id, "en");
        await servico.UploadAsync(video.Id, "pt-br", null, Vtt);

        Assert.Equal(2, (await servico.ListAsync(video.Id)).Count);
        Assert.Equal(["pt-br"], (await servico.ListPlayableAsync(video.Id)).Select(l => l.Language));
    }

    [Fact]
    public async Task Remover_apaga_o_arquivo()
    {
        var video = await CriarVideoAsync();
        var (servico, db, storage) = Criar();
        await using var _ = db;
        using var __ = storage;

        var legenda = await servico.UploadAsync(video.Id, "pt-br", null, Vtt);

        await servico.DeleteAsync(video.Id, legenda.Id);

        Assert.False(await storage.ExistsAsync(StorageBucket.Vod, legenda.StorageKey));

        await using var leitura = postgres.CreateContext();
        Assert.Empty(await leitura.VideoAssets.ToListAsync());
    }

    [Fact]
    public async Task Codigo_de_idioma_invalido_e_recusado()
    {
        var video = await CriarVideoAsync();
        var (servico, db, storage) = Criar();
        await using var _ = db;
        using var __ = storage;

        await Assert.ThrowsAsync<ArgumentException>(() => servico.RequestTranscriptionAsync(video.Id, "../../etc"));
        await Assert.ThrowsAsync<ArgumentException>(() => servico.UploadAsync(video.Id, "português", null, Vtt));
    }
}
