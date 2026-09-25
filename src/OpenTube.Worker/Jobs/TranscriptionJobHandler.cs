using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Queue;
using OpenTube.Infrastructure.Storage;
using OpenTube.Worker.Media;

namespace OpenTube.Worker.Jobs;

/// <summary>Parâmetros da transcrição.</summary>
/// <param name="VideoId">Vídeo a transcrever.</param>
/// <param name="OriginalKey">Arquivo de origem.</param>
public sealed record TranscriptionPayload(Guid VideoId, string OriginalKey);

/// <summary>
/// Gera a legenda a partir da fala e alimenta a busca com o texto. Procurar por uma frase dita
/// no vídeo é o que torna um acervo grande realmente navegável.
/// </summary>
public class TranscriptionJobHandler(
    OpenTubeDbContext db,
    IVideoStorage storage,
    ITranscriber transcritor,
    TimeProvider clock,
    ILogger<TranscriptionJobHandler> logger) : IJobHandler
{
    public JobKind Kind => JobKind.Transcript;

    public async Task HandleAsync(QueuedJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!transcritor.IsAvailable)
        {
            logger.LogInformation("Transcrição não configurada; trabalho descartado");
            return;
        }

        var payload = job.PayloadAs<TranscriptionPayload>()
            ?? throw new InvalidOperationException("Parâmetros de transcrição ausentes.");

        var video = await db.Videos
            .Include(v => v.Assets)
            .FirstOrDefaultAsync(v => v.Id == payload.VideoId, cancellationToken)
            ?? throw new InvalidOperationException($"Vídeo {payload.VideoId} não encontrado.");

        if (video.IsDeleted)
        {
            logger.LogInformation("Vídeo {VideoId} foi excluído; transcrição descartada", video.Id);
            return;
        }

        var trabalho = Directory.CreateTempSubdirectory($"opentube-legenda-{video.Id:n}-");

        try
        {
            var original = Path.Combine(trabalho.FullName, "original" + StorageKeys.SafeExtension(payload.OriginalKey));
            await storage.GetFileAsync(StorageBucket.Originals, payload.OriginalKey, original, cancellationToken);

            var transcricao = await transcritor.TranscribeAsync(original, trabalho.FullName, cancellationToken);

            var chave = StorageKeys.Caption(video.Id, transcricao.Language);
            await storage.PutFileAsync(StorageBucket.Vod, chave, transcricao.VttPath, MediaTypes.WebVtt, cancellationToken);

            // Substitui a legenda automática anterior em vez de acumular uma por execução.
            var existente = video.Assets.FirstOrDefault(a =>
                a.Kind == VideoAssetKind.Caption && a.StorageKey == chave);

            if (existente is null)
            {
                db.VideoAssets.Add(VideoAsset.Create(
                    video.Id, VideoAssetKind.Caption, chave, clock.GetUtcNow(),
                    transcricao.Language, "Legenda automática"));
            }

            video.SetTranscript(transcricao.Text);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Legenda gerada para o vídeo {VideoId}", video.Id);
        }
        finally
        {
            try
            {
                if (Directory.Exists(trabalho.FullName))
                    Directory.Delete(trabalho.FullName, recursive: true);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Não foi possível limpar a pasta de trabalho da transcrição");
            }
        }
    }
}
