// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using OpenTube.Domain.Access;
using OpenTube.Infrastructure.Playback;

namespace OpenTube.Web.Tests.Support;

/// <summary>
/// Endereços de reprodução obtidos como o player os obtém: a página dá o token, o pedido de
/// reprodução dá a playlist principal, que dá as versões, que dão os pedaços.
/// </summary>
public static partial class Reproducao
{
    /// <summary>Id do vídeo e token que a página entrega ao player.</summary>
    public static async Task<(Guid Video, string Token)> TokenDaPaginaAsync(HttpClient cliente, string slug)
    {
        var html = await cliente.GetStringAsync($"/watch/{slug}");
        var video = VideoRegex().Match(html);
        var token = TokenRegex().Match(html);

        Assert.True(video.Success && token.Success, $"a página de {slug} não traz o player");

        return (Guid.Parse(video.Groups[1].Value), WebUtility.HtmlDecode(token.Groups[1].Value));
    }

    /// <summary>O pedido que o player faz para começar a reprodução.</summary>
    public static Task<HttpResponseMessage> PedirAsync(HttpClient cliente, Guid video, string? token) =>
        cliente.PostAsJsonAsync("/api/play", new { video, token });

    /// <summary>Playlist principal, pelo caminho inteiro do player a partir da página.</summary>
    public static async Task<string> ManifestoDaPaginaAsync(HttpClient cliente, string slug)
    {
        var (video, token) = await TokenDaPaginaAsync(cliente, slug);
        var resposta = await PedirAsync(cliente, video, token);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var dados = await resposta.Content.ReadFromJsonAsync<RespostaDeReproducao>();
        return dados!.Src;
    }

    /// <summary>
    /// Playlist principal selada para <paramref name="espectador"/>, para quem a página não abre —
    /// e mostrar que o acesso continua sendo conferido mesmo com um selo legítimo.
    /// </summary>
    public static string ManifestoCom(OpenTubeWebFactory app, Guid videoId, Viewer espectador) =>
        "/api/m/" + Selos(app).Seal(PlaybackSealKind.Master, videoId, espectador);

    /// <summary>Playlist de uma versão selada para <paramref name="espectador"/>.</summary>
    public static string VersaoCom(OpenTubeWebFactory app, Guid videoId, string versao, Viewer espectador) =>
        "/api/p/" + Selos(app).Seal(PlaybackSealKind.Rendition, videoId, espectador, versao);

    /// <summary>Endereço opaco de um arquivo de versão (<c>versão/arquivo</c>) selado para <paramref name="espectador"/>.</summary>
    public static string SegmentoCom(OpenTubeWebFactory app, Guid videoId, string arquivo, Viewer espectador) =>
        "/s/" + Selos(app).Seal(PlaybackSealKind.Segment, videoId, espectador, arquivo);

    /// <summary>Endereço da primeira versão que a playlist principal anuncia.</summary>
    public static string PrimeiraVersao(string playlistPrincipal)
    {
        var linha = Linhas(playlistPrincipal).FirstOrDefault(l => l.StartsWith("/api/p/", StringComparison.Ordinal));

        Assert.NotNull(linha);

        return linha;
    }

    /// <summary>Página → pedido de reprodução → playlist principal → versão.</summary>
    public static async Task<string> VersaoPelaPaginaAsync(HttpClient cliente, string slug) =>
        PrimeiraVersao(await cliente.GetStringAsync(await ManifestoDaPaginaAsync(cliente, slug)));

    /// <summary>Primeiro pedaço do vídeo na playlist de uma versão.</summary>
    public static string PrimeiroSegmento(string playlistDaVersao) =>
        Linhas(playlistDaVersao).First(l => !l.StartsWith('#') && l.Length > 0);

    private static IEnumerable<string> Linhas(string playlist) => playlist.Split('\n').Select(l => l.Trim());

    private static PlaybackSeals Selos(OpenTubeWebFactory app) => app.Services.GetRequiredService<PlaybackSeals>();

    private sealed record RespostaDeReproducao(string Src);

    [GeneratedRegex("data-video=\"([^\"]+)\"")]
    private static partial Regex VideoRegex();

    [GeneratedRegex("data-reproducao=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();
}
