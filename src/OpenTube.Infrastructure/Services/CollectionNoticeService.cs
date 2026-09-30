// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Entities;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Services;

/// <summary>
/// Distintivo de vídeo novo na coleção. Ele aparece para quem já existia quando o vídeo
/// chegou, e some só quando essa pessoa abre o vídeo.
/// </summary>
public class CollectionNoticeService(OpenTubeDbContext db, TimeProvider clock)
{
    public async Task MarkSeenAsync(Guid userId, Guid videoId, CancellationToken cancellationToken = default)
    {
        var criado = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (DateTimeOffset?)u.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (criado is null)
            return;

        var colecoes = await db.CollectionVideos
            .Where(cv => cv.VideoId == videoId && cv.AddedAt > criado)
            .Select(cv => cv.CollectionId)
            .ToListAsync(cancellationToken);

        if (colecoes.Count == 0)
            return;

        var jaVistas = await db.CollectionVideoSeens
            .Where(s => s.UserId == userId && s.VideoId == videoId)
            .Select(s => s.CollectionId)
            .ToListAsync(cancellationToken);

        var agora = clock.GetUtcNow();
        foreach (var colecao in colecoes)
        {
            if (jaVistas.Contains(colecao))
                continue;

            db.CollectionVideoSeens.Add(CollectionVideoSeen.Mark(userId, colecao, videoId, agora));
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(cancellationToken);
    }
}
