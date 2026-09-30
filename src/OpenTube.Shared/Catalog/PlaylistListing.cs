// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Shared.Catalog;

/// <summary>
/// Vídeos de uma coleção na ordem da playlist, já filtrados pelo que o espectador pode ver.
/// </summary>
public sealed record PlaylistListing(
    string Slug,
    string Name,
    string? Description,
    IReadOnlyList<VideoSummary> Videos)
{
    /// <summary>O vídeo que vem a seguir na playlist, ou nenhum quando este é o último.</summary>
    public VideoSummary? NextAfter(Guid videoId)
    {
        for (var i = 0; i < Videos.Count - 1; i++)
        {
            if (Videos[i].Id == videoId)
                return Videos[i + 1];
        }

        return null;
    }

    public bool Contains(Guid videoId)
    {
        foreach (var video in Videos)
        {
            if (video.Id == videoId)
                return true;
        }

        return false;
    }
}
