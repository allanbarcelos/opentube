// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Entities;

/// <summary>
/// Vídeo marcado por uma pessoa. O favorito é dela: o mesmo vídeo sobe na lista de quem marcou
/// e continua no lugar de sempre para os demais. Se o vídeo está numa coleção, a marcação o
/// mostra também sozinho, e o clique continua abrindo a coleção nele.
/// </summary>
public class VideoFavorite
{
    private VideoFavorite() { }

    public Guid VideoId { get; private set; }
    public Guid UserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static VideoFavorite Mark(Guid videoId, Guid userId, DateTimeOffset now)
    {
        if (videoId == Guid.Empty)
            throw new ArgumentException("Video is required.", nameof(videoId));

        if (userId == Guid.Empty)
            throw new ArgumentException("User is required.", nameof(userId));

        return new VideoFavorite
        {
            VideoId = videoId,
            UserId = userId,
            CreatedAt = now
        };
    }
}
