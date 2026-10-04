// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Infrastructure.Access;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>
/// Entrada pelo link secreto de compartilhamento. O token vai para um cookie e é conferido a
/// cada requisição, para que a revogação tenha efeito imediato.
/// </summary>
public static class ShareEndpoints
{
    public static IEndpointRouteBuilder MapShareEndpoints(this IEndpointRouteBuilder rotas)
    {
        rotas.MapGet("/link/{token}", async (
            string token,
            AccessService acesso,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            // Se o link vale e para onde ele leva é decisão do serviço de acesso; aqui só fica
            // o cookie e o redirecionamento.
            if (await acesso.OpenShareLinkAsync(token, cancellationToken) is not { } entrada)
                return Results.Redirect("/not-found");

            contexto.Response.Cookies.Append(
                CurrentViewer.LinkCookieName,
                token,
                CurrentViewer.LinkCookieOptions(contexto.Request.IsHttps));

            return Results.Redirect(entrada.VideoSlug is { } endereco ? $"/watch/{endereco}" : "/");
        });

        return rotas;
    }
}
