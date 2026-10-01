// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Storage;

namespace OpenTube.Infrastructure.Services;

/// <summary>
/// Apaga vídeos de verdade: a linha, o que só existia por causa dela e os arquivos.
/// Não há registro para restaurar.
/// </summary>
public static class ExclusaoPermanenteDeVideo
{
    public static async Task ApagarRegistrosAsync(
        OpenTubeDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return;

        await db.AccessGrants
            .Where(g => g.TargetType == GrantTargetType.Video && g.TargetId != null && ids.Contains(g.TargetId.Value))
            .ExecuteDeleteAsync(cancellationToken);

        await db.Invitations
            .Where(i => i.TargetType == GrantTargetType.Video && i.TargetId != null && ids.Contains(i.TargetId.Value))
            .ExecuteDeleteAsync(cancellationToken);

        await db.PlaybackEvents.Where(e => ids.Contains(e.VideoId)).ExecuteDeleteAsync(cancellationToken);
        await db.PlaybackSessions.Where(s => ids.Contains(s.VideoId)).ExecuteDeleteAsync(cancellationToken);
        await db.VideoDailyStats.Where(s => ids.Contains(s.VideoId)).ExecuteDeleteAsync(cancellationToken);
        await db.VideoRetentionBuckets.Where(b => ids.Contains(b.VideoId)).ExecuteDeleteAsync(cancellationToken);
        await db.SupportThreads.Where(t => ids.Contains(t.VideoId)).ExecuteDeleteAsync(cancellationToken);
        await db.ProcessingJobs
            .Where(j => j.TargetId != null && ids.Contains(j.TargetId.Value))
            .ExecuteDeleteAsync(cancellationToken);
        await db.CollectionVideos.Where(v => ids.Contains(v.VideoId)).ExecuteDeleteAsync(cancellationToken);
        await db.Videos.Where(v => ids.Contains(v.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    public static async Task ApagarArquivosAsync(
        IVideoStorage storage, IEnumerable<Guid> ids, ILogger logger, CancellationToken cancellationToken)
    {
        foreach (var id in ids)
        {
            var prefixo = StorageKeys.VodPrefix(id);

            try
            {
                await storage.DeletePrefixAsync(StorageBucket.Originals, prefixo, cancellationToken);
                await storage.DeletePrefixAsync(StorageBucket.Vod, prefixo, cancellationToken);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Não foi possível apagar os arquivos do vídeo {VideoId}", id);
            }
        }
    }
}
