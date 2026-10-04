// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Localization;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Queue;
using OpenTube.Infrastructure.Storage;

namespace OpenTube.Infrastructure.Services;

/// <summary>Operações do administrador sobre um vídeo já enviado.</summary>
public class AdminVideoService(
    OpenTubeDbContext db,
    IJobQueue queue,
    IStorageReader storageReader,
    IStorageWriter storageWriter,
    ILogger<AdminVideoService> logger)
{
    public Task<Video?> FindAsync(Guid videoId, CancellationToken cancellationToken = default) =>
        db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, cancellationToken);

    /// <summary>
    /// Atualiza os dados de apresentação. O endereço legível não muda junto com o título:
    /// um link já compartilhado não pode parar de funcionar por causa de uma correção de
    /// digitação.
    /// </summary>
    public async Task<Video> UpdateAsync(
        Guid videoId,
        string title,
        string? description,
        IEnumerable<string>? tags,
        VideoVisibility visibility,
        CancellationToken cancellationToken = default)
    {
        var video = await CarregarAsync(videoId, cancellationToken);

        video.Describe(title, description);
        video.ReplaceTags(tags ?? []);
        video.ChangeVisibility(visibility);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Vídeo {VideoId} atualizado para visibilidade {Visibilidade}", videoId, visibility);

        return video;
    }

    /// <summary>Altera somente a visibilidade, usado nos atalhos da listagem.</summary>
    public async Task<Video> ChangeVisibilityAsync(Guid videoId, VideoVisibility visibility, CancellationToken cancellationToken = default)
    {
        var video = await CarregarAsync(videoId, cancellationToken);

        video.ChangeVisibility(visibility);
        await db.SaveChangesAsync(cancellationToken);

        return video;
    }

    /// <summary>Apaga o vídeo, os acessos e os arquivos. Devolve o título para a auditoria.</summary>
    public async Task<string> DeleteAsync(Guid videoId, CancellationToken cancellationToken = default)
    {
        var video = await CarregarAsync(videoId, cancellationToken);
        var titulo = video.Title;

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);
        await ExclusaoPermanenteDeVideo.ApagarRegistrosAsync(db, [videoId], cancellationToken);
        await transacao.CommitAsync(cancellationToken);

        await ExclusaoPermanenteDeVideo.ApagarArquivosAsync(storageWriter, [videoId], logger, cancellationToken);

        logger.LogInformation("Vídeo {VideoId} excluído", videoId);

        return titulo;
    }

    /// <summary>
    /// Recoloca o vídeo na fila a partir do arquivo original preservado. Serve tanto para
    /// tentar de novo depois de uma falha quanto para regerar as saídas.
    /// </summary>
    public async Task<Guid> RequeueAsync(Guid videoId, CancellationToken cancellationToken = default)
    {
        var video = await CarregarAsync(videoId, cancellationToken);

        if (video.Status is VideoStatus.Draft)
            throw new InvalidOperationException("This video's file has not finished uploading.");

        if (!await storageReader.ExistsAsync(StorageBucket.Originals, video.OriginalKey, cancellationToken))
            throw new InvalidOperationException("The original file is no longer in storage.");

        if (await TranscodificacaoEmAbertoAsync(video.Id, cancellationToken))
            throw new InvalidOperationException("This video is already in the transcoding queue.");

        var jobId = await queue.EnqueueAsync(
            JobKind.Transcode,
            video.Id,
            new TranscodePayload(video.Id, video.OriginalKey),
            cancellationToken: cancellationToken);

        logger.LogInformation("Vídeo {VideoId} recolocado na fila ({JobId})", videoId, jobId);

        return jobId;
    }

    /// <summary>Contagem de trabalhos por estado, exibida no painel.</summary>
    public Task<IReadOnlyDictionary<JobStatus, int>> QueueSummaryAsync(CancellationToken cancellationToken = default) =>
        queue.CountByStatusAsync(cancellationToken);

    private Task<bool> TranscodificacaoEmAbertoAsync(Guid videoId, CancellationToken cancellationToken) =>
        db.ProcessingJobs.AnyAsync(j =>
            j.Kind == JobKind.Transcode
            && j.TargetId == videoId
            && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running),
            cancellationToken);

    private async Task<Video> CarregarAsync(Guid videoId, CancellationToken cancellationToken) =>
        await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, cancellationToken)
        ?? throw new InvalidOperationException(LocalText.Format("Video {0} was not found.", videoId));
}
