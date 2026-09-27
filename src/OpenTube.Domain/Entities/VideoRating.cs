// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Entities;

/// <summary>
/// Quanto um vídeo foi útil para uma pessoa, de 1 a 5. Uma nota por pessoa e vídeo, que ela pode
/// trocar. Só a administração vê as notas: quem assiste vê apenas a própria.
/// </summary>
public class VideoRating
{
    public const int MinScore = 1;
    public const int MaxScore = 5;

    private VideoRating() { }

    public Guid VideoId { get; private set; }
    public Guid UserId { get; private set; }
    public int Score { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static VideoRating Rate(Guid videoId, Guid userId, int score, DateTimeOffset now)
    {
        Validar(score);

        return new VideoRating
        {
            VideoId = videoId,
            UserId = userId,
            Score = score,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void Change(int score, DateTimeOffset now)
    {
        Validar(score);
        Score = score;
        UpdatedAt = now;
    }

    public static bool IsValid(int score) => score is >= MinScore and <= MaxScore;

    private static void Validar(int score)
    {
        if (!IsValid(score))
            throw new ArgumentOutOfRangeException(nameof(score), score, "The rating goes from 1 to 5.");
    }
}
