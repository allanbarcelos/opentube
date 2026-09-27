// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Media;

/// <summary>
/// Monta o conjunto de versões geradas para um vídeo. Nunca aumenta a resolução do original
/// — gastar CPU para inventar pixels só piora a imagem e engorda o storage.
/// </summary>
public static class RenditionLadder
{
    /// <summary>Duração de cada segmento HLS, em segundos.</summary>
    public const double SegmentSeconds = 4;

    private static readonly (int Quality, int VideoKbps, int AudioKbps)[] Steps =
    [
        (360, 800, 96),
        (480, 1400, 128),
        (720, 2800, 128),
        (1080, 5000, 192)
    ];

    /// <summary>
    /// Versões a gerar para um original com as dimensões informadas. O degrau é escolhido pelo
    /// menor lado, para que vídeo em pé (retrato) não seja tratado como 1080p só por ter
    /// 1920 pixels de altura.
    /// </summary>
    public static IReadOnlyList<Rendition> For(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "As dimensões do original precisam ser positivas.");

        var isPortrait = sourceHeight > sourceWidth;
        var sourceQuality = Math.Min(sourceWidth, sourceHeight);
        var aspect = (double)sourceWidth / sourceHeight;

        var ladder = new List<Rendition>();

        foreach (var (quality, videoKbps, audioKbps) in Steps)
        {
            if (quality > sourceQuality)
                continue;

            // O degrau dita o menor lado; o outro sai da proporção do original.
            var (width, height) = isPortrait
                ? (Even(quality), Even(quality / aspect))
                : (Even(quality * aspect), Even(quality));

            ladder.Add(new Rendition($"{quality}p", width, height, videoKbps, audioKbps));
        }

        // Original menor que o degrau mais baixo: gera uma única versão na resolução nativa,
        // porque mesmo um vídeo pequeno precisa existir em HLS para ser reproduzido.
        if (ladder.Count == 0)
        {
            var (_, videoKbps, audioKbps) = Steps[0];
            var scale = (double)sourceQuality / Steps[0].Quality;
            ladder.Add(new Rendition(
                $"{sourceQuality}p",
                Even(sourceWidth),
                Even(sourceHeight),
                Math.Max(200, (int)(videoKbps * scale)),
                audioKbps));
        }

        return ladder;
    }

    /// <summary>
    /// Intervalo entre quadros-chave. Precisa ser múltiplo exato da duração do segmento nas
    /// quatro versões, senão o player trava ao trocar de qualidade no meio da reprodução.
    /// </summary>
    public static int KeyFrameInterval(double frameRate)
    {
        var fps = frameRate is > 0 and < 240 ? frameRate : 30;
        return Math.Max(1, (int)Math.Round(fps * SegmentSeconds));
    }

    private static int Even(double value)
    {
        var rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded % 2 != 0)
            rounded++;
        return Math.Max(2, rounded);
    }
}
