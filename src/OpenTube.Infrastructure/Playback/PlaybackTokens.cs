// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Globalization;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Access;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Security;

namespace OpenTube.Infrastructure.Playback;

/// <summary>Para que serve um token de reprodução.</summary>
public enum PlaybackTokenKind
{
    /// <summary>Emitido pela página do vídeo; abre a playlist principal.</summary>
    Page = 0,

    /// <summary>Emitido pela playlist principal; vale para as versões e os segmentos.</summary>
    Playback = 1
}

/// <summary>
/// Tokens que amarram a reprodução à página do vídeo. A playlist principal só sai com o token
/// que a página emitiu, e as versões e os segmentos só com o que a playlist principal emitiu:
/// copiar o endereço do vídeo para outro programa exige tirar um token da página, e ele vence.
/// O token é assinado com o segredo do servidor e preso ao vídeo e a quem assiste; não
/// substitui a política de acesso, que continua sendo conferida a cada pedido.
/// </summary>
public class PlaybackTokens(IOptions<SecurityOptions> options, TimeProvider clock)
{
    /// <summary>Nome do parâmetro do endereço que leva o token.</summary>
    public const string QueryName = "t";

    /// <summary>Tempo entre abrir a página e o player pedir a playlist principal.</summary>
    public static readonly TimeSpan PageLifetime = TimeSpan.FromHours(1);

    /// <summary>Duração de uma reprodução, pausas incluídas.</summary>
    public static readonly TimeSpan PlaybackLifetime = TimeSpan.FromHours(12);

    private readonly SecurityOptions _options = options.Value;

    /// <summary>
    /// Quem assiste, do jeito que o token guarda: a conta, o link secreto apresentado ou o
    /// visitante anônimo de um vídeo público.
    /// </summary>
    public static string ViewerKey(Viewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        return viewer.UserId is { } usuario ? "u" + usuario.ToString("n")
            : viewer.LinkGrantId is { } link ? "l" + link.ToString("n")
            : "anon";
    }

    public string Issue(PlaybackTokenKind kind, Guid videoId, Viewer viewer)
    {
        var validade = kind is PlaybackTokenKind.Page ? PageLifetime : PlaybackLifetime;
        var expira = (clock.GetUtcNow() + validade).ToUnixTimeSeconds();

        return expira.ToString(CultureInfo.InvariantCulture) + "." + Assinar(kind, videoId, ViewerKey(viewer), expira);
    }

    /// <summary>Confere o token. Formato errado, assinatura errada ou prazo vencido valem não.</summary>
    public bool Validate(string? token, PlaybackTokenKind kind, Guid videoId, Viewer viewer)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var partes = token.Split('.');

        if (partes.Length != 2
            || !long.TryParse(partes[0], NumberStyles.None, CultureInfo.InvariantCulture, out var expira))
            return false;

        DateTimeOffset instante;
        try
        {
            instante = DateTimeOffset.FromUnixTimeSeconds(expira);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if (instante <= clock.GetUtcNow())
            return false;

        return TokenHasher.Verify(Conteudo(kind, videoId, ViewerKey(viewer), expira), Base64Padrao(partes[1]), _options.TokenPepper);
    }

    private string Assinar(PlaybackTokenKind kind, Guid videoId, string espectador, long expira) =>
        TokenHasher.Hash(Conteudo(kind, videoId, espectador, expira), _options.TokenPepper)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Conteudo(PlaybackTokenKind kind, Guid videoId, string espectador, long expira) =>
        $"token-de-reproducao|{(int)kind}|{videoId:n}|{espectador}|{expira.ToString(CultureInfo.InvariantCulture)}";

    private static string Base64Padrao(string assinatura)
    {
        var padrao = assinatura.Replace('-', '+').Replace('_', '/');

        return padrao.PadRight(padrao.Length + (4 - padrao.Length % 4) % 4, '=');
    }
}
