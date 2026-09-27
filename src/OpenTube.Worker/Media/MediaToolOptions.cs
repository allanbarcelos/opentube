// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Worker.Media;

/// <summary>Onde estão as ferramentas de mídia e onde o worker trabalha os arquivos.</summary>
public class MediaToolOptions
{
    public const string SectionName = "Media";

    public string FfmpegPath { get; set; } = "ffmpeg";

    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>
    /// Pasta de trabalho. Fica vazia por padrão para usar a pasta temporária do sistema;
    /// em produção costuma apontar para um disco com espaço para o arquivo original.
    /// </summary>
    public string? WorkDirectory { get; set; }

    /// <summary>Tempo máximo de uma transcodificação antes de considerar travada.</summary>
    public TimeSpan TranscodeTimeout { get; set; } = TimeSpan.FromHours(6);
}
