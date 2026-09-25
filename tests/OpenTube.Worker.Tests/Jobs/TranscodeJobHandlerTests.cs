using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Queue;
using OpenTube.Infrastructure.Services;
using OpenTube.Infrastructure.Storage;
using OpenTube.TestSupport;
using OpenTube.Worker.Jobs;
using OpenTube.Worker.Media;
using OpenTube.Worker.Tests.Support;

namespace OpenTube.Worker.Tests.Jobs;

/// <summary>
/// Percurso completo do worker: arquivo no storage, transcodificação real com FFmpeg, saídas
/// enviadas e vídeo marcado como pronto.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class TranscodeJobHandlerTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime, IDisposable
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();

    private readonly FakeTimeProvider _relogio = new(Agora);
    private readonly string _trabalho = Directory.CreateTempSubdirectory("opentube-worker-").FullName;

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_trabalho, recursive: true);
        }
        catch (IOException)
        {
            // A pasta temporária pode continuar ocupada; o sistema operacional limpa depois.
        }
    }

    private TranscodeJobHandler Criar(OpenTube.Infrastructure.Persistence.OpenTubeDbContext db, IVideoStorage storage)
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var opcoes = Microsoft.Extensions.Options.Options.Create(new MediaToolOptions());
        var pipeline = new TranscodePipeline(runner, new FfprobeMediaProbe(runner, opcoes), opcoes, NullLogger<TranscodePipeline>.Instance);

        return new TranscodeJobHandler(db, storage, pipeline, _relogio, NullLogger<TranscodeJobHandler>.Instance);
    }

    private async Task<Video> PrepararVideoAsync(IVideoStorage storage, double segundos = 5, int largura = 640, int altura = 360)
    {
        var videoId = Guid.CreateVersion7();
        var chave = StorageKeys.Original(videoId, "amostra.mp4");
        var arquivo = await MediaTools.CreateSampleAsync(_trabalho, segundos, largura, altura, fileName: $"{videoId:n}.mp4");

        await storage.PutFileAsync(StorageBucket.Originals, chave, arquivo, MediaTypes.Mp4);

        await using var db = postgres.CreateContext();
        var video = Video.CreateDraft("Amostra", $"amostra-{videoId:n}"[..40], chave, Admin, Agora, id: videoId);
        video.MarkUploaded(new FileInfo(arquivo).Length);
        db.Videos.Add(video);
        await db.SaveChangesAsync();

        return video;
    }

    private static QueuedJob Job(Video video) =>
        new(Guid.CreateVersion7(), JobKind.Transcode, video.Id,
            System.Text.Json.JsonSerializer.Serialize(new TranscodePayload(video.Id, video.OriginalKey)), 1);

    [FfmpegFact]
    public async Task Transcodifica_envia_as_saidas_e_marca_o_video_como_pronto()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video));

        await using var leitura = postgres.CreateContext();
        var pronto = await leitura.Videos.SingleAsync(v => v.Id == video.Id);

        Assert.Equal(VideoStatus.Ready, pronto.Status);
        Assert.StartsWith($"{video.Id}/r-", pronto.HlsPrefix);
        Assert.Equal(640, pronto.Width);
        Assert.Equal(360, pronto.Height);
        Assert.InRange(pronto.DurationSeconds, 4.5, 5.5);
        Assert.Equal(Agora, pronto.PublishedAt);

        var prefixo = pronto.HlsPrefix!;
        var chaves = await storage.ListAsync(StorageBucket.Vod, prefixo);

        Assert.Contains(StorageKeys.MasterUnder(prefixo), chaves);
        Assert.Contains(StorageKeys.RenditionPlaylistUnder(prefixo, "360p"), chaves);
        Assert.Contains(StorageKeys.ThumbnailUnder(prefixo), chaves);
        Assert.Contains(StorageKeys.SpriteUnder(prefixo), chaves);
        Assert.Contains(StorageKeys.SpriteMetadataUnder(prefixo), chaves);
        Assert.Contains(chaves, k => k.EndsWith(".m4s"));
        Assert.Equal(StorageKeys.ThumbnailUnder(prefixo), pronto.ThumbnailKey);
    }

    [FfmpegFact]
    public async Task O_video_continua_privado_depois_de_processado()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video));

        await using var leitura = postgres.CreateContext();

        // Terminar o processamento não pode liberar nada: a visibilidade é decisão do
        // administrador, nunca efeito colateral do pipeline.
        Assert.Equal(VideoVisibility.Private, (await leitura.Videos.SingleAsync(v => v.Id == video.Id)).Visibility);
    }

    [FfmpegFact]
    public async Task A_playlist_principal_anuncia_todas_as_versoes()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage, segundos: 5, largura: 1280, altura: 720);

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video));

        await using var leitura = postgres.CreateContext();
        var prefixo = (await leitura.Videos.SingleAsync(v => v.Id == video.Id)).HlsPrefix!;
        var master = await storage.GetTextAsync(StorageBucket.Vod, StorageKeys.MasterUnder(prefixo));

        Assert.Contains("360p/stream.m3u8", master);
        Assert.Contains("480p/stream.m3u8", master);
        Assert.Contains("720p/stream.m3u8", master);
        Assert.Contains("#EXT-X-STREAM-INF", master);
    }

    private async Task<string> PrefixoEmUsoAsync(Guid videoId)
    {
        await using var leitura = postgres.CreateContext();

        return (await leitura.Videos.SingleAsync(v => v.Id == videoId)).HlsPrefix!;
    }

    [FfmpegFact]
    public async Task Reprocessar_troca_a_geracao_e_apaga_as_anteriores_sem_tocar_nas_legendas()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage, segundos: 5, largura: 1280, altura: 720);

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video));

        var anterior = await PrefixoEmUsoAsync(video.Id);

        // Restos do layout antigo, gravado direto na raiz do vídeo, e uma legenda enviada
        // pelo administrador, que não é gerada pela transcodificação.
        await storage.PutTextAsync(StorageBucket.Vod, $"{video.Id}/2160p/stream.m3u8", "#EXTM3U", MediaTypes.HlsPlaylist);
        await storage.PutTextAsync(StorageBucket.Vod, StorageKeys.Caption(video.Id, "pt"), "WEBVTT", MediaTypes.WebVtt);

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video));

        var atual = await PrefixoEmUsoAsync(video.Id);
        var chaves = await storage.ListAsync(StorageBucket.Vod, StorageKeys.VodPrefix(video.Id));

        Assert.NotEqual(anterior, atual);
        Assert.DoesNotContain(chaves, k => k.StartsWith(anterior, StringComparison.Ordinal));
        Assert.DoesNotContain(chaves, k => k.Contains("2160p"));
        Assert.Contains(StorageKeys.MasterUnder(atual), chaves);
        Assert.Contains(StorageKeys.Caption(video.Id, "pt"), chaves);
    }

    [FfmpegFact]
    public async Task Reprocessamento_que_falha_mantem_a_versao_anterior_no_ar()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video));

        var anterior = await PrefixoEmUsoAsync(video.Id);

        // Sem o original, o reprocessamento falha logo no download.
        await storage.DeletePrefixAsync(StorageBucket.Originals, video.OriginalKey);

        await using (var db = postgres.CreateContext())
            await Assert.ThrowsAnyAsync<Exception>(() => Criar(db, storage).HandleAsync(Job(video)));

        await using var leitura = postgres.CreateContext();
        var depois = await leitura.Videos.SingleAsync(v => v.Id == video.Id);

        Assert.Equal(VideoStatus.Ready, depois.Status);
        Assert.Equal(anterior, depois.HlsPrefix);
        Assert.True(await storage.ExistsAsync(StorageBucket.Vod, StorageKeys.MasterUnder(anterior)));
    }

    [FfmpegFact]
    public async Task Retoma_video_que_ficou_em_processamento_quando_o_worker_morreu()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);

        await using (var db = postgres.CreateContext())
        {
            var interrompido = await db.Videos.SingleAsync(v => v.Id == video.Id);
            interrompido.StartProcessing();
            await db.SaveChangesAsync();
        }

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video));

        await using var leitura = postgres.CreateContext();
        Assert.Equal(VideoStatus.Ready, (await leitura.Videos.SingleAsync(v => v.Id == video.Id)).Status);
    }

    [FfmpegFact]
    public async Task Arquivo_corrompido_marca_o_video_como_falho_e_propaga_o_erro()
    {
        using var storage = minio.CreateStorage();
        var videoId = Guid.CreateVersion7();
        var chave = StorageKeys.Original(videoId, "corrompido.mp4");

        await storage.PutTextAsync(StorageBucket.Originals, chave, "isto não é um vídeo", MediaTypes.Mp4);

        await using (var db = postgres.CreateContext())
        {
            var video = Video.CreateDraft("Corrompido", "corrompido", chave, Admin, Agora, id: videoId);
            video.MarkUploaded(20);
            db.Videos.Add(video);
            await db.SaveChangesAsync();
        }

        await using (var db = postgres.CreateContext())
        {
            var job = new QueuedJob(Guid.CreateVersion7(), JobKind.Transcode, videoId,
                System.Text.Json.JsonSerializer.Serialize(new TranscodePayload(videoId, chave)), 1);

            await Assert.ThrowsAsync<InvalidOperationException>(() => Criar(db, storage).HandleAsync(job));
        }

        await using var leitura = postgres.CreateContext();
        Assert.Equal(VideoStatus.Failed, (await leitura.Videos.SingleAsync(v => v.Id == videoId)).Status);
    }

    [Fact]
    public async Task Vídeo_excluído_durante_a_espera_na_fila_é_descartado()
    {
        using var storage = minio.CreateStorage();
        var videoId = Guid.CreateVersion7();

        await using (var db = postgres.CreateContext())
        {
            var video = Video.CreateDraft("Excluído", "excluido", "chave", Admin, Agora, id: videoId);
            video.MarkUploaded(10);
            video.SoftDelete(Agora);
            db.Videos.Add(video);
            await db.SaveChangesAsync();
        }

        await using (var db = postgres.CreateContext())
        {
            var job = new QueuedJob(Guid.CreateVersion7(), JobKind.Transcode, videoId,
                System.Text.Json.JsonSerializer.Serialize(new TranscodePayload(videoId, "chave")), 1);

            await Criar(db, storage).HandleAsync(job);
        }

        await using var leitura = postgres.CreateContext();
        Assert.Equal(VideoStatus.Uploaded, (await leitura.Videos.SingleAsync(v => v.Id == videoId)).Status);
    }

    [Fact]
    public async Task Recusa_trabalho_sem_parametros()
    {
        using var storage = minio.CreateStorage();
        await using var db = postgres.CreateContext();

        var job = new QueuedJob(Guid.CreateVersion7(), JobKind.Transcode, null, "{}", 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Criar(db, storage).HandleAsync(job));
    }

    [Fact]
    public async Task Recusa_trabalho_para_video_inexistente()
    {
        using var storage = minio.CreateStorage();
        await using var db = postgres.CreateContext();

        var job = new QueuedJob(Guid.CreateVersion7(), JobKind.Transcode, Guid.CreateVersion7(),
            System.Text.Json.JsonSerializer.Serialize(new TranscodePayload(Guid.CreateVersion7(), "chave")), 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Criar(db, storage).HandleAsync(job));
    }
}
