// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Media;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Services;

/// <summary>Sumário (capítulos) dos vídeos: montado na administração, mostrado a quem assiste.</summary>
public class VideoChapterService(OpenTubeDbContext db, ILogger<VideoChapterService> logger)
{
    public async Task<IReadOnlyList<Chapter>> ListAsync(Guid videoId, CancellationToken cancellationToken = default) =>
        await db.VideoChapters
            .AsNoTracking()
            .Where(c => c.VideoId == videoId)
            .OrderBy(c => c.Position)
            .Select(c => new Chapter(c.StartSeconds, c.Title))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Troca o sumário inteiro pelo que veio do editor, conferido contra a duração do vídeo.
    /// Sem nenhuma linha preenchida, o vídeo fica sem sumário.
    /// </summary>
    /// <exception cref="ChapterException">Uma linha inválida; nada é gravado.</exception>
    public async Task<IReadOnlyList<Chapter>> ReplaceAsync(
        Guid videoId, IEnumerable<(string? Start, string? Title)> rows, CancellationToken cancellationToken = default)
    {
        var duracao = await db.Videos
            .Where(v => v.Id == videoId)
            .Select(v => (double?)v.DurationSeconds)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Video not found");

        var capitulos = VideoChapters.Parse(rows, TimeSpan.FromSeconds(duracao));

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.VideoChapters.Where(c => c.VideoId == videoId).ExecuteDeleteAsync(cancellationToken);
        db.VideoChapters.AddRange(capitulos.Select((c, i) => VideoChapter.Create(videoId, i, c)));
        await db.SaveChangesAsync(cancellationToken);

        await transacao.CommitAsync(cancellationToken);

        logger.LogInformation("Sumário do vídeo {VideoId} com {Quantos} capítulo(s)", videoId, capitulos.Count);

        return capitulos;
    }
}
