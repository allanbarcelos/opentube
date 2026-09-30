// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Entities;

/// <summary>
/// Coleção marcada por uma pessoa. O favorito é dela, não do acervo: a mesma coleção pode
/// estar no topo para quem a marcou e no meio da lista para os demais.
/// </summary>
public class CollectionFavorite
{
    private CollectionFavorite() { }

    public Guid CollectionId { get; private set; }
    public Guid UserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static CollectionFavorite Mark(Guid collectionId, Guid userId, DateTimeOffset now)
    {
        if (collectionId == Guid.Empty)
            throw new ArgumentException("Collection is required.", nameof(collectionId));

        if (userId == Guid.Empty)
            throw new ArgumentException("User is required.", nameof(userId));

        return new CollectionFavorite
        {
            CollectionId = collectionId,
            UserId = userId,
            CreatedAt = now
        };
    }
}
