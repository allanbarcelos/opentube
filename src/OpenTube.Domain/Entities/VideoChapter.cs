// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Media;

namespace OpenTube.Domain.Entities;

/// <summary>Capítulo do sumário de um vídeo, como guardado. O sumário é trocado inteiro ao salvar.</summary>
public class VideoChapter
{
    private VideoChapter() { }

    public Guid Id { get; private set; }
    public Guid VideoId { get; private set; }

    /// <summary>Ordem no sumário (a do tempo de início).</summary>
    public int Position { get; private set; }

    public int StartSeconds { get; private set; }
    public string Title { get; private set; } = string.Empty;

    public static VideoChapter Create(Guid videoId, int position, Chapter chapter)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(chapter.Title);
        ArgumentOutOfRangeException.ThrowIfNegative(chapter.StartSeconds);

        return new VideoChapter
        {
            Id = Guid.CreateVersion7(),
            VideoId = videoId,
            Position = position,
            StartSeconds = chapter.StartSeconds,
            Title = chapter.Title
        };
    }

    public Chapter ToChapter() => new(StartSeconds, Title);
}
