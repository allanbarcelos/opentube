using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Options;
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
    IJobQueue queue,
    IOptions<StorageOptions> storageOptions,
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

        // Dois trabalhos do mesmo vídeo apagam a geração um do outro. O de identificador
        // menor (o mais antigo, em Guid versão 7) segue; o outro encerra sem publicar.
        var emAndamento = await db.ProcessingJobs.AsNoTracking()
            .Where(j => j.Id != job.Id
                && j.Kind == JobKind.Transcode
                && j.TargetId == video.Id
                && j.Status == JobStatus.Running)
            .Select(j => j.Id)
            .ToListAsync(cancellationToken);

        if (emAndamento.Any(id => id.CompareTo(job.Id) < 0))
        {
            logger.LogInformation("Vídeo {VideoId} já está sendo transcodificado; trabalho {JobId} encerrado", video.Id, job.Id);
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
        }
        catch (Exception e)
        {
            // O desligamento do worker também cai aqui; a marcação precisa chegar ao banco
            // mesmo com o cancelamento já pedido.
            await RegistrarFalhaAsync(video, e);

            // Só apaga o prefixo desta tentativa quando ele não é o que o banco publicou.
            // Um save que já commitou e depois falhou no processo não pode levar a geração
            // que acabou de entrar no ar.
            if (!await EhOPrefixoPublicadoAsync(video.Id, prefixo))
                await TentarApagarAsync(storage.DeletePrefixAsync(StorageBucket.Vod, prefixo, CancellationToken.None), prefixo);

            throw;
        }
        finally
        {
            TentarApagar(trabalho.FullName);
        }

        if (!publicado)
            return;

        logger.LogInformation("Vídeo {VideoId} pronto em {Prefixo}", video.Id, prefixo);

        try
        {
            // As URLs já assinadas da geração anterior continuam válidas até o fim do prazo.
            // A limpeza só corre depois disso, e só se esta geração seguir sendo a publicada.
            await queue.EnqueueAsync(
                JobKind.RetireOutputs,
                video.Id,
                new RetireOutputsPayload(video.Id, prefixo),
                storageOptions.Value.PlaybackUrlLifetime,
                CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Não foi possível agendar a limpeza das saídas antigas de {VideoId}", video.Id);
        }
    }

    private Task<bool> EhOPrefixoPublicadoAsync(Guid videoId, string prefixo) =>
        db.Videos.AsNoTracking().AnyAsync(v => v.Id == videoId && v.HlsPrefix == prefixo, CancellationToken.None);

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
