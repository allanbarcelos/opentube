// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Infrastructure.Storage;

/// <summary>
/// Monta os caminhos dos arquivos no storage. Centralizar isto evita que cada parte do sistema
/// invente um layout próprio e impede que o nome do arquivo enviado pelo navegador vire caminho.
/// </summary>
public static class StorageKeys
{
    /// <summary>Arquivo original, preservado para permitir reprocessamento.</summary>
    public static string Original(Guid videoId, string? originalFileName)
        => $"{videoId}/source{SafeExtension(originalFileName)}";

    /// <summary>Prefixo de todas as saídas de reprodução de um vídeo.</summary>
    public static string VodPrefix(Guid videoId) => $"{videoId}/";

    /// <summary>
    /// Prefixo de uma geração de saídas. Cada processamento grava numa pasta nova e só depois
    /// troca a versão em uso: reprocessar não tira o vídeo do ar, e uma falha no meio não
    /// destrói a versão que já funcionava.
    /// </summary>
    public static string OutputPrefix(Guid videoId, Guid generation) => $"{videoId}/r-{generation:n}/";

    /// <summary>Pasta das legendas, que não pertencem a nenhuma geração de saídas.</summary>
    public static string CaptionsPrefix(Guid videoId) => $"{videoId}/captions/";

    /// <summary>Playlist principal, a que o player recebe.</summary>
    public static string Master(Guid videoId) => MasterUnder(VodPrefix(videoId));

    public static string MasterUnder(string prefix) => $"{prefix}master.m3u8";

    /// <summary>Playlist de uma das versões do ladder.</summary>
    public static string RenditionPlaylist(Guid videoId, string rendition)
        => RenditionPlaylistUnder(VodPrefix(videoId), rendition);

    public static string RenditionPlaylistUnder(string prefix, string rendition)
        => $"{prefix}{Sanitize(rendition)}/stream.m3u8";

    /// <summary>Prefixo dos segmentos de uma versão.</summary>
    public static string RenditionPrefix(Guid videoId, string rendition)
        => RenditionPrefixUnder(VodPrefix(videoId), rendition);

    public static string RenditionPrefixUnder(string prefix, string rendition)
        => $"{prefix}{Sanitize(rendition)}/";

    public static string Thumbnail(Guid videoId) => ThumbnailUnder(VodPrefix(videoId));

    /// <summary>
    /// Miniatura enviada para uma coleção. A versão entra no caminho: trocar a imagem não
    /// reaproveita o endereço antigo.
    /// </summary>
    public static string CollectionThumbnail(Guid collectionId, long version) =>
        $"collections/{collectionId:n}/thumb-{version}.jpg";

    public static string ThumbnailUnder(string prefix) => $"{prefix}thumb.jpg";

    public static string Sprite(Guid videoId) => SpriteUnder(VodPrefix(videoId));

    public static string SpriteUnder(string prefix) => $"{prefix}sprite.jpg";

    public static string SpriteMetadata(Guid videoId) => SpriteMetadataUnder(VodPrefix(videoId));

    public static string SpriteMetadataUnder(string prefix) => $"{prefix}sprite.vtt";

    public static string Caption(Guid videoId, string language)
        => $"{CaptionsPrefix(videoId)}{Sanitize(language)}.vtt";

    /// <summary>
    /// Extensão do arquivo enviado, limitada a letras e números. O nome original vem do
    /// navegador e não pode influenciar o caminho gravado.
    /// </summary>
    public static string SafeExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return ".bin";

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 10)
            return ".bin";

        var limpa = new string(extension[1..].Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        return limpa.Length == 0 ? ".bin" : "." + limpa;
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "default";

        var limpo = new string(value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());

        return limpo.Length == 0 ? "default" : limpo.ToLowerInvariant();
    }
}
