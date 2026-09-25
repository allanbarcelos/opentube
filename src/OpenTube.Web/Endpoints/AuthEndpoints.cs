using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Security;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>
/// Entrada e saída do sistema. As telas enviam formulários comuns e são redirecionadas de
/// volta: assim a sessão é criada no próprio HttpContext, sem depender de JavaScript.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder rotas)
    {
        rotas.MapPost("/entrar/codigo", async (
            [FromForm] string email,
            PasswordlessAuthService auth,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var resultado = await auth.RequestCodeAsync(
                email,
                AuthPurpose.Login,
                contexto.Connection.RemoteIpAddress?.ToString(),
                cancellationToken: cancellationToken);

            // Endereço conhecido e desconhecido recebem a mesma resposta: a diferença
            // entregaria a lista de convidados a quem perguntasse.
            return resultado.Failure switch
            {
                AuthFailure.InvalidEmail => Redirecionar(email, erro: "Endereço de email inválido."),
                AuthFailure.RateLimited => Redirecionar(email, erro: "Pedidos demais. Aguarde alguns minutos."),
                _ => Redirecionar(email, enviado: true)
            };
        });

        rotas.MapPost("/entrar/verificar", async (
            [FromForm] string email,
            [FromForm] string codigo,
            PasswordlessAuthService auth,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var resultado = await auth.VerifyCodeAsync(
                email,
                codigo,
                contexto.Connection.RemoteIpAddress?.ToString(),
                contexto.Request.Headers.UserAgent.ToString(),
                cancellationToken);

            if (!resultado.Succeeded)
                return Redirecionar(email, enviado: true, erro: Mensagem(resultado.Failure));

            await EntrarAsync(contexto, resultado);

            return Results.Redirect(resultado.User!.IsAdmin ? "/admin" : "/");
        });

        rotas.MapGet("/entrar/{token}", async (
            string token,
            PasswordlessAuthService auth,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var resultado = await auth.VerifyTokenAsync(
                token,
                contexto.Connection.RemoteIpAddress?.ToString(),
                contexto.Request.Headers.UserAgent.ToString(),
                cancellationToken);

            if (!resultado.Succeeded)
                return Redirecionar(null, erro: Mensagem(resultado.Failure));

            await EntrarAsync(contexto, resultado);

            return Results.Redirect(resultado.User!.IsAdmin ? "/admin" : "/");
        });

        rotas.MapPost("/sair", async (PasswordlessAuthService auth, HttpContext contexto) =>
        {
            var sessionId = ViewerContext.SessionId(contexto.User);

            if (sessionId is not null)
                await auth.RevokeSessionAsync(sessionId.Value);

            await contexto.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            return Results.Redirect("/");
        });

        return rotas;
    }

    private static IResult Redirecionar(string? email, bool enviado = false, string? erro = null)
    {
        var parametros = new List<string>();

        if (!string.IsNullOrWhiteSpace(email))
            parametros.Add("email=" + Uri.EscapeDataString(email));

        if (enviado)
            parametros.Add("enviado=1");

        if (!string.IsNullOrWhiteSpace(erro))
            parametros.Add("erro=" + Uri.EscapeDataString(erro));

        return Results.Redirect("/entrar" + (parametros.Count > 0 ? "?" + string.Join('&', parametros) : string.Empty));
    }

    private static Task EntrarAsync(HttpContext contexto, SignInOutcome resultado) =>
        contexto.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            SessionAuthentication.BuildPrincipal(resultado.User!, resultado.Session!.Id),
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = resultado.Session.ExpiresAt
            });

    private static string Mensagem(AuthFailure falha) => falha switch
    {
        AuthFailure.InvalidEmail => "Endereço de email inválido.",
        AuthFailure.CodeExpired => "Este código expirou. Peça um novo.",
        AuthFailure.CodeAlreadyUsed => "Este link já foi usado. Peça um novo código.",
        AuthFailure.TooManyAttempts => "Tentativas demais. Peça um novo código.",
        AuthFailure.UserDisabled => "Este acesso está desativado.",
        AuthFailure.RateLimited => "Pedidos demais. Aguarde alguns minutos.",
        _ => "Código inválido."
    };
}
