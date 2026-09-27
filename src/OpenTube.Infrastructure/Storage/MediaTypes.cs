// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Infrastructure.Storage;

/// <summary>Tipos de mídia aceitos no envio e usados ao servir as saídas.</summary>
public static class MediaTypes
{
    public const string HlsPlaylist = "application/vnd.apple.mpegurl";
    public const string HlsSegment = "video/iso.segment";
    public const string Mp4 = "video/mp4";
    public const string WebVtt = "text/vtt";
    public const string Jpeg = "image/jpeg";

    private static readonly HashSet<string> ExtensoesAceitas = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".mpg", ".mpeg", ".wmv", ".flv", ".ts", ".mts", ".m2ts"
    };

    /// <summary>
    /// Verifica se o arquivo parece ser vídeo. A conferência de verdade acontece no worker,
    /// com <c>ffprobe</c> — aqui a intenção é só barrar o engano óbvio antes de gastar o envio.
    /// </summary>
    public static bool LooksLikeVideo(string? fileName, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType) &&
            contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrWhiteSpace(fileName) &&
               ExtensoesAceitas.Contains(Path.GetExtension(fileName));
    }

    /// <summary>Tipo de mídia de um arquivo gerado pela transcodificação, pela extensão.</summary>
    public static string ForOutput(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".m3u8" => HlsPlaylist,
        ".m4s" => HlsSegment,
        ".mp4" => Mp4,
        ".vtt" => WebVtt,
        ".jpg" or ".jpeg" => Jpeg,
        _ => "application/octet-stream"
    };
}
