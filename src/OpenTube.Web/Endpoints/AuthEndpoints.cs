// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Localization;
using OpenTube.Infrastructure.Security;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>
/// Entrada e saída do sistema. As telas enviam formulários comuns e são redirecionadas de
/// volta: assim a sessão é criada no próprio HttpContext, sem depender de JavaScript.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>
    /// Cookie com a chave deste navegador. O código pedido aqui guarda o resumo dela, e o link
    /// do email só entra num navegador que a traga.
    /// </summary>
    public const string BrowserCookieName = "opentube.navegador";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder rotas)
    {
        rotas.MapPost("/sign-in/code", async (
            [FromForm] string email,
            [FromForm] string? voltar,
            PasswordlessAuthService auth,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var resultado = await auth.RequestCodeAsync(
                email,
                AuthPurpose.Login,
                contexto.Connection.RemoteIpAddress?.ToString(),
                browserKey: ChaveDoNavegador(contexto),
                cancellationToken: cancellationToken);

            // Endereço conhecido e desconhecido recebem a mesma resposta: a diferença
            // entregaria a lista de convidados a quem perguntasse.
            return resultado.Failure switch
            {
                AuthFailure.InvalidEmail => Redirecionar(email, voltar, erro: LocalText.Get("Invalid email address.")),
                AuthFailure.RateLimited => Redirecionar(email, voltar, erro: LocalText.Get("Too many requests. Wait a few minutes.")),
                _ => Redirecionar(email, voltar, enviado: true)
            };
        });

        rotas.MapPost("/sign-in/verify", async (
            [FromForm] string email,
            [FromForm] string codigo,
            [FromForm] string? voltar,
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
                return Redirecionar(email, voltar, enviado: true, erro: Mensagem(resultado.Failure));

            await EntrarAsync(contexto, resultado);

            // O convite leva ao vídeo liberado; só vale caminho do próprio site.
            return Results.Redirect(Retorno.EhLocal(voltar) ? voltar! : resultado.User!.IsAdmin ? "/admin" : "/");
        });

        // O GET do link mostra só a confirmação (EntrarPeloLink.razor): filtros de email abrem
        // todo link recebido, e um GET que entrasse queimaria o link e abriria sessão para o filtro.
        rotas.MapPost("/sign-in/link", async (
            [FromForm] string token,
            PasswordlessAuthService auth,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var resultado = await auth.VerifyTokenAsync(
                token,
                contexto.Request.Cookies[BrowserCookieName],
                contexto.Connection.RemoteIpAddress?.ToString(),
                contexto.Request.Headers.UserAgent.ToString(),
                cancellationToken);

            // Noutro navegador o link não entra, mas o código do mesmo email entra: a tela já
            // abre pedindo o código para o endereço certo.
            if (resultado.Failure is AuthFailure.OtherBrowser)
                return Redirecionar(resultado.Email, null, enviado: true, erro: Mensagem(resultado.Failure));

            if (!resultado.Succeeded)
                return Redirecionar(null, null, erro: Mensagem(resultado.Failure));

            await EntrarAsync(contexto, resultado);

            return Results.Redirect(resultado.User!.IsAdmin ? "/admin" : "/");
        });

        rotas.MapPost("/sign-out", async (PasswordlessAuthService auth, HttpContext contexto) =>
        {
            var sessionId = ViewerContext.SessionId(contexto.User);

            if (sessionId is not null)
                await auth.RevokeSessionAsync(sessionId.Value);

            await contexto.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            return Results.Redirect("/");
        });

        return rotas;
    }

    private static IResult Redirecionar(string? email, string? voltar, bool enviado = false, string? erro = null)
    {
        var parametros = new List<string>();

        if (!string.IsNullOrWhiteSpace(email))
            parametros.Add("email=" + Uri.EscapeDataString(email));

        if (Retorno.EhLocal(voltar))
            parametros.Add("voltar=" + Uri.EscapeDataString(voltar!));

        if (enviado)
            parametros.Add("enviado=1");

        if (!string.IsNullOrWhiteSpace(erro))
            parametros.Add("erro=" + Uri.EscapeDataString(erro));

        return Results.Redirect("/sign-in" + (parametros.Count > 0 ? "?" + string.Join('&', parametros) : string.Empty));
    }

    /// <summary>
    /// Chave deste navegador, criada no primeiro pedido de código e reaproveitada depois, para
    /// que códigos pedidos em abas diferentes do mesmo navegador continuem valendo pelo link.
    /// </summary>
    private static string ChaveDoNavegador(HttpContext contexto)
    {
        var chave = contexto.Request.Cookies[BrowserCookieName];

        if (string.IsNullOrWhiteSpace(chave) || chave.Length > 100)
            chave = OneTimeCode.GenerateToken();

        contexto.Response.Cookies.Append(BrowserCookieName, chave, new CookieOptions
        {
            MaxAge = TimeSpan.FromDays(30),
            IsEssential = true,
            HttpOnly = true,
            Secure = contexto.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/sign-in"
        });

        return chave;
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
        AuthFailure.InvalidEmail => LocalText.Get("Invalid email address."),
        AuthFailure.CodeExpired => LocalText.Get("This code has expired. Ask for a new one."),
        AuthFailure.CodeAlreadyUsed => LocalText.Get("This link has already been used. Ask for a new code."),
        AuthFailure.TooManyAttempts => LocalText.Get("Too many attempts. Ask for a new code."),
        AuthFailure.LockedOut => LocalText.Get("Too many wrong codes today. Use the link in the email, or try again tomorrow."),
        AuthFailure.UserDisabled => LocalText.Get("This access is disabled."),
        AuthFailure.RateLimited => LocalText.Get("Too many requests. Wait a few minutes."),
        AuthFailure.OtherBrowser => LocalText.Get("The email link only signs in on the browser where the code was requested. To sign in here, type the code from the email."),
        _ => LocalText.Get("Invalid code.")
    };
}
