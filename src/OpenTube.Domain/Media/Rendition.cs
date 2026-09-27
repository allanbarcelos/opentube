// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Media;

/// <summary>Uma das versões geradas na transcodificação adaptativa.</summary>
/// <param name="Name">Rótulo usado no caminho do storage e na playlist (ex.: <c>720p</c>).</param>
/// <param name="Width">Largura em pixels, sempre par.</param>
/// <param name="Height">Altura em pixels, sempre par.</param>
/// <param name="VideoBitrateKbps">Alvo de taxa de bits do vídeo.</param>
/// <param name="AudioBitrateKbps">Alvo de taxa de bits do áudio.</param>
public readonly record struct Rendition(string Name, int Width, int Height, int VideoBitrateKbps, int AudioBitrateKbps)
{
    /// <summary>Taxa máxima usada no controle de buffer do codificador.</summary>
    public int MaxRateKbps => (int)(VideoBitrateKbps * 1.07);

    /// <summary>Tamanho do buffer do codificador.</summary>
    public int BufferSizeKbps => VideoBitrateKbps * 2;

    /// <summary>Largura de banda anunciada na playlist mestra, somando vídeo, áudio e sobrecarga.</summary>
    public int ManifestBandwidthBps => (int)((VideoBitrateKbps + AudioBitrateKbps) * 1.1 * 1000);

    public string Resolution => $"{Width}x{Height}";
}
