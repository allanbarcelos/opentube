// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Entities;

/// <summary>
/// A pessoa clicou num vídeo que chegou numa coleção depois dela. O distintivo da coleção
/// fica enquanto houver algum vídeo novo que ela ainda não abriu.
/// </summary>
public class CollectionVideoSeen
{
    private CollectionVideoSeen() { }

    public Guid UserId { get; private set; }
    public Guid CollectionId { get; private set; }
    public Guid VideoId { get; private set; }
    public DateTimeOffset SeenAt { get; private set; }

    public static CollectionVideoSeen Mark(Guid userId, Guid collectionId, Guid videoId, DateTimeOffset now)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("User is required.", nameof(userId));

        if (collectionId == Guid.Empty)
            throw new ArgumentException("Collection is required.", nameof(collectionId));

        if (videoId == Guid.Empty)
            throw new ArgumentException("Video is required.", nameof(videoId));

        return new CollectionVideoSeen
        {
            UserId = userId,
            CollectionId = collectionId,
            VideoId = videoId,
            SeenAt = now
        };
    }
}
