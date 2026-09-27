// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Access;
using OpenTube.Domain.Entities;
using OpenTube.Infrastructure.Access;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Services;

/// <summary>As notas de um vídeo, como a administração as vê.</summary>
/// <param name="Count">Quantas pessoas avaliaram.</param>
/// <param name="Average">Média, de 1 a 5; nula sem avaliações.</param>
/// <param name="Distribution">Quantas notas de cada valor: índice 0 para a nota 1, até 4 para a nota 5.</param>
public sealed record RatingSummary(int Count, double? Average, IReadOnlyList<int> Distribution)
{
    public static RatingSummary Empty { get; } = new(0, null, [0, 0, 0, 0, 0]);
}

/// <summary>
/// Avaliação de utilidade dos vídeos. Avalia quem entrou com email e pode assistir ao vídeo;
/// cada pessoa vê só a própria nota, e a administração vê o conjunto.
/// </summary>
public class VideoRatingService(OpenTubeDbContext db, AccessService acesso, TimeProvider clock)
{
    public async Task<int?> GetMineAsync(Guid videoId, Viewer viewer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        if (viewer.UserId is not { } userId)
            return null;

        return await db.VideoRatings
            .Where(r => r.VideoId == videoId && r.UserId == userId)
            .Select(r => (int?)r.Score)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Grava a nota, ou troca a que a pessoa já tinha dado.</summary>
    public async Task<int> RateAsync(Guid videoId, Viewer viewer, int score, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        if (viewer.UserId is not { } userId)
            throw new InvalidOperationException("Sign in to rate this video.");

        if (!VideoRating.IsValid(score))
            throw new InvalidOperationException("The rating goes from 1 to 5.");

        var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, cancellationToken)
            ?? throw new InvalidOperationException("Video not found");

        // Quem não pode assistir também não avalia, e ouve o mesmo "não encontrado".
        if (!(await acesso.EvaluateAsync(viewer, video, cancellationToken)).Allowed)
            throw new InvalidOperationException("Video not found");

        var agora = clock.GetUtcNow();

        // Uma instrução só: dois cliques rápidos não criam duas notas.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO video_ratings (video_id, user_id, score, created_at, updated_at)
            VALUES ({videoId}, {userId}, {score}, {agora}, {agora})
            ON CONFLICT (video_id, user_id) DO UPDATE
               SET score = EXCLUDED.score, updated_at = EXCLUDED.updated_at
            """, cancellationToken);

        return score;
    }

    public async Task<RatingSummary> SummaryAsync(Guid videoId, CancellationToken cancellationToken = default)
    {
        var contagem = await db.VideoRatings
            .Where(r => r.VideoId == videoId)
            .GroupBy(r => r.Score)
            .Select(g => new { Nota = g.Key, Quantas = g.Count() })
            .ToListAsync(cancellationToken);

        if (contagem.Count == 0)
            return RatingSummary.Empty;

        var distribuicao = Enumerable.Range(VideoRating.MinScore, VideoRating.MaxScore)
            .Select(nota => contagem.FirstOrDefault(c => c.Nota == nota)?.Quantas ?? 0)
            .ToList();

        var total = distribuicao.Sum();
        var media = (double)contagem.Sum(c => c.Nota * c.Quantas) / total;

        return new RatingSummary(total, Math.Round(media, 1), distribuicao);
    }
}
