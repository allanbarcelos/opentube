// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using OpenTube.Domain.Access;
using OpenTube.Infrastructure.Playback;

namespace OpenTube.Web.Tests.Support;

/// <summary>
/// Endereços de reprodução obtidos como o player os obtém: a página do vídeo dá a playlist
/// principal com o token dela, e a principal dá as versões com o token da reprodução.
/// </summary>
public static partial class Reproducao
{
    /// <summary>Playlist principal que a página do vídeo entrega ao player.</summary>
    public static async Task<string> ManifestoDaPaginaAsync(HttpClient cliente, string slug)
    {
        var html = await cliente.GetStringAsync($"/watch/{slug}");
        var achado = ManifestoRegex().Match(html);

        Assert.True(achado.Success, $"a página de {slug} não traz o endereço do vídeo");

        return WebUtility.HtmlDecode(achado.Groups[1].Value);
    }

    /// <summary>
    /// Playlist principal com um token emitido para <paramref name="espectador"/>, para quem a
    /// página não abre — e mostrar que o acesso continua sendo conferido mesmo com token.
    /// </summary>
    public static string ManifestoCom(OpenTubeWebFactory app, Guid videoId, Viewer espectador) =>
        $"/api/videos/{videoId}/master.m3u8?{PlaybackTokens.QueryName}="
        + Uri.EscapeDataString(app.Services.GetRequiredService<PlaybackTokens>().Issue(PlaybackTokenKind.Page, videoId, espectador));

    /// <summary>Playlist de uma versão com um token de reprodução emitido para <paramref name="espectador"/>.</summary>
    public static string VersaoCom(OpenTubeWebFactory app, Guid videoId, string versao, Viewer espectador) =>
        $"/api/videos/{videoId}/renditions/{versao}.m3u8?{PlaybackTokens.QueryName}="
        + Uri.EscapeDataString(app.Services.GetRequiredService<PlaybackTokens>().Issue(PlaybackTokenKind.Playback, videoId, espectador));

    /// <summary>Endereço de uma versão como a playlist principal aponta para ela.</summary>
    public static string VersaoDaPlaylist(string playlistPrincipal, string versao)
    {
        var linha = playlistPrincipal.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Contains($"/renditions/{versao}.m3u8", StringComparison.Ordinal));

        Assert.NotNull(linha);

        return linha;
    }

    /// <summary>Página do vídeo → playlist principal → versão: o caminho inteiro do player.</summary>
    public static async Task<string> VersaoPelaPaginaAsync(HttpClient cliente, string slug, string versao)
    {
        var principal = await cliente.GetStringAsync(await ManifestoDaPaginaAsync(cliente, slug));

        return VersaoDaPlaylist(principal, versao);
    }

    /// <summary>Primeiro segmento da playlist de uma versão, com o token que ela pôs nele.</summary>
    public static string PrimeiroSegmento(string playlistDaVersao) =>
        playlistDaVersao.Split('\n')
            .Select(l => l.Trim())
            .First(l => l.Contains(".m4s", StringComparison.Ordinal));

    [GeneratedRegex("data-manifest=\"([^\"]+)\"")]
    private static partial Regex ManifestoRegex();
}
