// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.AspNetCore.Http;
using OpenTube.Domain.Access;
using OpenTube.Infrastructure.Access;
using OpenTube.Infrastructure.Playback;

namespace OpenTube.Web.Auth;

/// <summary>
/// Monta o espectador da requisição: a identidade do cookie de sessão mais, quando houver, a
/// concessão comprovada pelo link secreto e as reproduções já abertas. O resultado é
/// memorizado por requisição para não repetir a conferência do token a cada consulta.
/// </summary>
public class CurrentViewer(IHttpContextAccessor contexto, AccessService acesso, PlaybackTickets bilhetes)
{
    /// <summary>Cookie que guarda o token do link de compartilhamento apresentado.</summary>
    public const string LinkCookieName = "opentube.link";

    private Viewer? _memorizado;

    public async Task<Viewer> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_memorizado is not null)
            return _memorizado;

        var http = contexto.HttpContext;
        var espectador = ViewerContext.From(http?.User);

        if (http?.Request.Cookies.TryGetValue(LinkCookieName, out var token) == true && !string.IsNullOrWhiteSpace(token))
            espectador = await acesso.ResolveLinkAsync(espectador, token, cancellationToken);

        if (http is not null)
            espectador = bilhetes.Apply(espectador,
                http.Request.Cookies.Where(c => c.Key.StartsWith(PlaybackTickets.CookiePrefix, StringComparison.Ordinal)));

        return _memorizado = espectador;
    }

    /// <summary>Opções do cookie do link: dura a sessão do navegador e não é lido por script.</summary>
    public static CookieOptions LinkCookieOptions(bool secure) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = secure,
        Path = "/",
        IsEssential = true
    };

    /// <summary>
    /// Grava o bilhete da reprodução recém-aberta. Um cookie por vídeo, com o prazo do próprio
    /// bilhete; não é lido por script.
    /// </summary>
    public static void AppendTicket(HttpContext http, IssuedTicket bilhete) =>
        http.Response.Cookies.Append(bilhete.CookieName, bilhete.Value, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            Path = "/",
            Expires = bilhete.ExpiresAt,
            IsEssential = true
        });
}
