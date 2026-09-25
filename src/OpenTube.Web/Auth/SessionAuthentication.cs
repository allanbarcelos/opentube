using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using OpenTube.Domain.Entities;
using OpenTube.Infrastructure.Security;

namespace OpenTube.Web.Auth;

/// <summary>
/// Autenticação por cookie apoiada em sessões guardadas no banco. O cookie sozinho não basta:
/// a cada requisição a sessão é conferida, para que revogar um acesso tenha efeito imediato
/// em vez de esperar o cookie expirar.
/// </summary>
public static class SessionAuthentication
{
    public const string CookieName = "opentube.sessao";

    public static IServiceCollection AddSessionAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromDays(30);
                options.LoginPath = "/entrar";
                options.LogoutPath = "/sair";
                options.AccessDeniedPath = "/entrar";

                options.Events.OnValidatePrincipal = async contexto =>
                {
                    var sessionId = ViewerContext.SessionId(contexto.Principal);

                    if (sessionId is null)
                    {
                        contexto.RejectPrincipal();
                        return;
                    }

                    var auth = contexto.HttpContext.RequestServices.GetRequiredService<PasswordlessAuthService>();
                    var atual = await auth.TouchSessionAsync(sessionId.Value, contexto.HttpContext.RequestAborted);

                    if (atual is null)
                    {
                        contexto.RejectPrincipal();
                        await contexto.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                        return;
                    }

                    // O papel pode ter mudado desde a entrada; o cookie não pode ficar com
                    // uma permissão que a pessoa já perdeu.
                    contexto.ReplacePrincipal(BuildPrincipal(atual.Value.User, sessionId.Value));
                    contexto.ShouldRenew = true;
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Administrator, policy => policy.RequireClaim(OpenTubeClaims.IsAdmin, "1"));

        return services;
    }

    public static ClaimsPrincipal BuildPrincipal(User user, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(user);

        var identidade = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);

        identidade.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identidade.AddClaim(new Claim(ClaimTypes.Email, user.Email));
        identidade.AddClaim(new Claim(ClaimTypes.Name, user.DisplayName ?? user.Email));
        identidade.AddClaim(new Claim(OpenTubeClaims.SessionId, sessionId.ToString()));

        if (user.IsAdmin)
            identidade.AddClaim(new Claim(OpenTubeClaims.IsAdmin, "1"));

        return new ClaimsPrincipal(identidade);
    }
}

/// <summary>Políticas de autorização da aplicação.</summary>
public static class Policies
{
    public const string Administrator = "administrador";
}
