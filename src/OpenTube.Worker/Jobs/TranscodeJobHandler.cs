using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Queue;
using OpenTube.Infrastructure.Services;
using OpenTube.Infrastructure.Storage;
using OpenTube.Worker.Media;

namespace OpenTube.Worker.Jobs;

/// <summary>Executor de um tipo de trabalho da fila.</summary>
public interface IJobHandler
{
    JobKind Kind { get; }

    Task HandleAsync(QueuedJob job, CancellationToken cancellationToken = default);
}

/// <summary>
/// Baixa o original, transcodifica, envia as saídas e marca o vídeo como pronto. Cada etapa é
/// idempotente o bastante para que uma nova tentativa não deixe lixo: a pasta de trabalho é
/// recriada e as saídas anteriores do vídeo são apagadas antes do envio.
/// </summary>
public class TranscodeJobHandler(
    OpenTubeDbContext db,
    IVideoStorage storage,
    TranscodePipeline pipeline,
    TimeProvider clock,
    ILogger<TranscodeJobHandler> logger) : IJobHandler
{
    public JobKind Kind => JobKind.Transcode;

    public async Task HandleAsync(QueuedJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var payload = job.PayloadAs<TranscodePayload>()
            ?? throw new InvalidOperationException("Parâmetros de transcodificação ausentes.");

        var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == payload.VideoId, cancellationToken)
            ?? throw new InvalidOperationException($"Vídeo {payload.VideoId} não encontrado.");

        if (video.IsDeleted)
        {
            logger.LogInformation("Vídeo {VideoId} foi excluído; transcodificação descartada", video.Id);
            return;
        }

        video.StartProcessing();
        await db.SaveChangesAsync(cancellationToken);

        var trabalho = Directory.CreateTempSubdirectory($"opentube-{video.Id:n}-");

        try
        {
            var original = Path.Combine(trabalho.FullName, "original" + StorageKeys.SafeExtension(payload.OriginalKey));
            await storage.GetFileAsync(StorageBucket.Originals, payload.OriginalKey, original, cancellationToken);

            var saida = await pipeline.RunAsync(original, trabalho.FullName, cancellationToken);

            // Limpa saídas de um processamento anterior: se o ladder encolheu, uma versão
            // antiga sobreviveria e continuaria sendo anunciada na playlist.
            await storage.DeletePrefixAsync(StorageBucket.Vod, StorageKeys.VodPrefix(video.Id), cancellationToken);

            await EnviarSaidasAsync(video.Id, saida, cancellationToken);

            video.MarkReady(
                StorageKeys.VodPrefix(video.Id),
                saida.Info.DurationSeconds,
                saida.Info.Width,
                saida.Info.Height,
                StorageKeys.Thumbnail(video.Id),
                StorageKeys.Sprite(video.Id),
                clock.GetUtcNow());

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Vídeo {VideoId} pronto em {Versoes} versões", video.Id, saida.Ladder.Count);
        }
        catch
        {
            video.MarkFailed();
            await db.SaveChangesAsync(cancellationToken);
            throw;
        }
        finally
        {
            TentarApagar(trabalho.FullName);
        }
    }

    private async Task EnviarSaidasAsync(Guid videoId, TranscodeOutput saida, CancellationToken cancellationToken)
    {
        var prefixo = StorageKeys.VodPrefix(videoId);

        foreach (var arquivo in Directory.EnumerateFiles(saida.OutputDirectory, "*", SearchOption.AllDirectories))
        {
            var relativo = Path.GetRelativePath(saida.OutputDirectory, arquivo).Replace(Path.DirectorySeparatorChar, '/');

            await storage.PutFileAsync(
                StorageBucket.Vod,
                prefixo + relativo,
                arquivo,
                MediaTypes.ForOutput(arquivo),
                cancellationToken);
        }

        await storage.PutFileAsync(StorageBucket.Vod, StorageKeys.Thumbnail(videoId), saida.ThumbnailPath, MediaTypes.Jpeg, cancellationToken);
        await storage.PutFileAsync(StorageBucket.Vod, StorageKeys.Sprite(videoId), saida.SpritePath, MediaTypes.Jpeg, cancellationToken);
        await storage.PutFileAsync(StorageBucket.Vod, StorageKeys.SpriteMetadata(videoId), saida.SpriteVttPath, MediaTypes.WebVtt, cancellationToken);
    }

    private void TentarApagar(string pasta)
    {
        try
        {
            if (Directory.Exists(pasta))
                Directory.Delete(pasta, recursive: true);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Não foi possível limpar a pasta de trabalho {Pasta}", pasta);
        }
    }
}
