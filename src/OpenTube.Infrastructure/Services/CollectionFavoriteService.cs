// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Entities;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Services;

/// <summary>Marca e desmarca coleções favoritas de uma pessoa.</summary>
public class CollectionFavoriteService(OpenTubeDbContext db, TimeProvider clock)
{
    /// <summary>Inverte a marcação. Devolve se a coleção ficou favorita.</summary>
    public async Task<bool> ToggleAsync(Guid userId, Guid collectionId, CancellationToken cancellationToken = default)
    {
        var favorito = await db.CollectionFavorites.FirstOrDefaultAsync(
            f => f.UserId == userId && f.CollectionId == collectionId, cancellationToken);

        if (favorito is not null)
        {
            db.CollectionFavorites.Remove(favorito);
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }

        db.CollectionFavorites.Add(CollectionFavorite.Mark(collectionId, userId, clock.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
