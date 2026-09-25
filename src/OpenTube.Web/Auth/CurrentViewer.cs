using Microsoft.AspNetCore.Http;
using OpenTube.Domain.Access;
using OpenTube.Infrastructure.Access;

namespace OpenTube.Web.Auth;

/// <summary>
/// Monta o espectador da requisição: a identidade do cookie de sessão mais, quando houver, a
/// concessão comprovada pelo link secreto. O resultado é memorizado por requisição para não
/// repetir a conferência do token a cada consulta.
/// </summary>
public class CurrentViewer(IHttpContextAccessor contexto, AccessService acesso)
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
}
