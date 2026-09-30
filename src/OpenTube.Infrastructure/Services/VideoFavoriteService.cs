// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Entities;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Services;

/// <summary>Marca e desmarca vídeos favoritos de uma pessoa.</summary>
public class VideoFavoriteService(OpenTubeDbContext db, TimeProvider clock)
{
    /// <summary>Inverte a marcação. Devolve se o vídeo ficou favorito.</summary>
    public async Task<bool> ToggleAsync(Guid userId, Guid videoId, CancellationToken cancellationToken = default)
    {
        var favorito = await db.VideoFavorites.FirstOrDefaultAsync(
            f => f.UserId == userId && f.VideoId == videoId, cancellationToken);

        if (favorito is not null)
        {
            db.VideoFavorites.Remove(favorito);
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }

        db.VideoFavorites.Add(VideoFavorite.Mark(videoId, userId, clock.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public Task<bool> IsFavoriteAsync(Guid userId, Guid videoId, CancellationToken cancellationToken = default) =>
        db.VideoFavorites.AnyAsync(f => f.UserId == userId && f.VideoId == videoId, cancellationToken);
}
