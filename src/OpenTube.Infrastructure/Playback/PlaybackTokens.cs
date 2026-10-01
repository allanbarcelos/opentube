// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Globalization;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Access;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Security;

namespace OpenTube.Infrastructure.Playback;

/// <summary>
/// Token que a página do vídeo entrega ao player. Com ele, e só com ele, o player pede o
/// endereço da reprodução: a página não traz endereço de vídeo nenhum, e quem não passou por ela
/// não tem por onde começar. O token é assinado com o segredo do servidor e preso ao vídeo e a
/// quem assiste; não substitui a política de acesso, que continua sendo conferida a cada pedido.
/// </summary>
public class PlaybackTokens(IOptions<SecurityOptions> options, TimeProvider clock)
{
    /// <summary>Tempo entre abrir a página e o player pedir a reprodução.</summary>
    public static readonly TimeSpan PageLifetime = TimeSpan.FromHours(1);

    private readonly SecurityOptions _options = options.Value;

    /// <summary>
    /// Quem assiste, do jeito que tokens e selos guardam: a conta, o link secreto apresentado ou
    /// o visitante anônimo de um vídeo público.
    /// </summary>
    public static string ViewerKey(Viewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        return viewer.UserId is { } usuario ? "u" + usuario.ToString("n")
            : viewer.LinkGrantId is { } link ? "l" + link.ToString("n")
            : "anon";
    }

    public string Issue(Guid videoId, Viewer viewer)
    {
        var expira = (clock.GetUtcNow() + PageLifetime).ToUnixTimeSeconds();

        return expira.ToString(CultureInfo.InvariantCulture) + "." + Assinar(videoId, ViewerKey(viewer), expira);
    }

    /// <summary>Confere o token. Formato errado, assinatura errada ou prazo vencido valem não.</summary>
    public bool Validate(string? token, Guid videoId, Viewer viewer)
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

        return TokenHasher.Verify(Conteudo(videoId, ViewerKey(viewer), expira), Base64Padrao(partes[1]), _options.TokenPepper);
    }

    private string Assinar(Guid videoId, string espectador, long expira) =>
        TokenHasher.Hash(Conteudo(videoId, espectador, expira), _options.TokenPepper)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Conteudo(Guid videoId, string espectador, long expira) =>
        $"token-da-pagina|{videoId:n}|{espectador}|{expira.ToString(CultureInfo.InvariantCulture)}";

    private static string Base64Padrao(string assinatura)
    {
        var padrao = assinatura.Replace('-', '+').Replace('_', '/');

        return padrao.PadRight(padrao.Length + (4 - padrao.Length % 4) % 4, '=');
    }
}
