using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenTube.Domain.Entities;
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
/// Baixa o original, transcodifica, envia as saídas e marca o vídeo como pronto. Cada
/// processamento grava numa pasta nova e só troca a versão em uso depois de tudo enviado: um
/// reprocessamento não tira o vídeo do ar, e uma falha no meio não destrói a versão que já
/// funcionava. As gerações anteriores são apagadas só depois da troca.
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
        var prefixo = StorageKeys.OutputPrefix(video.Id, Guid.CreateVersion7());
        var publicado = false;

        try
        {
            var original = Path.Combine(trabalho.FullName, "original" + StorageKeys.SafeExtension(payload.OriginalKey));
            await storage.GetFileAsync(StorageBucket.Originals, payload.OriginalKey, original, cancellationToken);

            var saida = await pipeline.RunAsync(original, trabalho.FullName, cancellationToken);

            await EnviarSaidasAsync(prefixo, saida, cancellationToken);

            video.MarkReady(
                prefixo,
                saida.Info.DurationSeconds,
                saida.Info.Width,
                saida.Info.Height,
                StorageKeys.ThumbnailUnder(prefixo),
                StorageKeys.SpriteUnder(prefixo),
                clock.GetUtcNow());

            await db.SaveChangesAsync(cancellationToken);
            publicado = true;

            logger.LogInformation("Vídeo {VideoId} pronto em {Versoes} versões", video.Id, saida.Ladder.Count);
        }
        catch (Exception e)
        {
            // O desligamento do worker também cai aqui; a marcação precisa chegar ao banco
            // mesmo com o cancelamento já pedido.
            await RegistrarFalhaAsync(video, e);
            await TentarApagarAsync(storage.DeletePrefixAsync(StorageBucket.Vod, prefixo, CancellationToken.None), prefixo);
            throw;
        }
        finally
        {
            TentarApagar(trabalho.FullName);
        }

        if (publicado)
            await TentarApagarAsync(ApagarGeracoesAntigasAsync(video.Id, prefixo), StorageKeys.VodPrefix(video.Id));
    }

    /// <summary>
    /// Num vídeo que nunca ficou pronto, a falha vira estado <c>Failed</c>. Num vídeo já
    /// pronto, a versão anterior continua no ar e o estado não muda: a falha fica registrada
    /// no trabalho da fila.
    /// </summary>
    private async Task RegistrarFalhaAsync(Video video, Exception erro)
    {
        if (db.Entry(video).State is EntityState.Modified)
            await db.Entry(video).ReloadAsync(CancellationToken.None);

        if (video.Status is VideoStatus.Ready)
        {
            logger.LogWarning(erro, "Reprocessamento do vídeo {VideoId} falhou; a versão anterior segue no ar", video.Id);
            return;
        }

        video.MarkFailed();
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Apaga as saídas que não pertencem à geração em uso: gerações anteriores e o layout
    /// antigo, gravado direto na raiz do vídeo. As legendas ficam, porque não são geradas aqui.
    /// </summary>
    private async Task ApagarGeracoesAntigasAsync(Guid videoId, string emUso)
    {
        var legendas = StorageKeys.CaptionsPrefix(videoId);
        var chaves = await storage.ListAsync(StorageBucket.Vod, StorageKeys.VodPrefix(videoId), CancellationToken.None);

        var antigas = chaves
            .Where(k => !k.StartsWith(emUso, StringComparison.Ordinal) && !k.StartsWith(legendas, StringComparison.Ordinal))
            .ToList();

        if (antigas.Count > 0)
            await storage.DeleteKeysAsync(StorageBucket.Vod, antigas, CancellationToken.None);
    }

    private async Task EnviarSaidasAsync(string prefixo, TranscodeOutput saida, CancellationToken cancellationToken)
    {
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

        await storage.PutFileAsync(StorageBucket.Vod, StorageKeys.ThumbnailUnder(prefixo), saida.ThumbnailPath, MediaTypes.Jpeg, cancellationToken);
        await storage.PutFileAsync(StorageBucket.Vod, StorageKeys.SpriteUnder(prefixo), saida.SpritePath, MediaTypes.Jpeg, cancellationToken);
        await storage.PutFileAsync(StorageBucket.Vod, StorageKeys.SpriteMetadataUnder(prefixo), saida.SpriteVttPath, MediaTypes.WebVtt, cancellationToken);
    }

    /// <summary>Limpeza no storage é melhor esforço: falhar nela não desfaz o processamento.</summary>
    private async Task TentarApagarAsync(Task limpeza, string prefixo)
    {
        try
        {
            await limpeza;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Não foi possível limpar saídas antigas em {Prefixo}", prefixo);
        }
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
