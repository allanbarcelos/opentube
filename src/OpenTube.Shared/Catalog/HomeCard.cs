// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Shared.Catalog;

/// <summary>O que um cartão da página inicial representa.</summary>
public enum HomeCardKind
{
    /// <summary>Um vídeo que não pertence a nenhuma coleção.</summary>
    Video = 0,

    /// <summary>Uma coleção, no lugar dos vídeos que estão nela.</summary>
    Collection = 1
}

/// <summary>Cartão da página inicial: um vídeo avulso ou uma coleção.</summary>
public sealed record HomeCard(
    HomeCardKind Kind,
    Guid Id,
    string Slug,
    string Title,
    string? Description,
    int VideoCount,
    double DurationSeconds,
    int Visibility,
    int Status,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Tags,
    bool IsFavorite)
{
    /// <summary>O mesmo cartão visto como vídeo, para reutilizar o cartão da listagem.</summary>
    public VideoSummary ToVideo() => new(
        Id, Slug, Title, Description, DurationSeconds, Visibility, Status, PublishedAt, CreatedAt, Tags);
}
