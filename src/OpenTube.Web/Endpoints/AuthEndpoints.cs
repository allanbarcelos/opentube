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

        rotas.MapGet("/sign-in/{token}", async (
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
        _ => LocalText.Get("Invalid code.")
    };
}
