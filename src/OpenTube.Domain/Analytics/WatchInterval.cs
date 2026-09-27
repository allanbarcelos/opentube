// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Analytics;

/// <summary>
/// Um trecho do vídeo efetivamente assistido, em segundos desde o início.
/// </summary>
/// <param name="Start">Começo do trecho.</param>
/// <param name="End">Fim do trecho.</param>
public readonly record struct WatchInterval(double Start, double End)
{
    public double Duration => Math.Max(0, End - Start);

    public bool IsEmpty => End <= Start;

    /// <summary>Cria um trecho já ordenado e limitado à duração do vídeo.</summary>
    public static WatchInterval Create(double start, double end, double videoDuration)
    {
        if (end < start)
            (start, end) = (end, start);

        var limite = videoDuration > 0 ? videoDuration : double.MaxValue;

        return new WatchInterval(
            Math.Clamp(start, 0, limite),
            Math.Clamp(end, 0, limite));
    }

    public bool Contains(double position) => position >= Start && position < End;
}
