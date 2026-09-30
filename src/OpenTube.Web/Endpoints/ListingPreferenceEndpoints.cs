// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.AspNetCore.Mvc;

namespace OpenTube.Web.Endpoints;

/// <summary>
/// Preferência de listagem. Vale para quem entrou e para quem não entrou: é só jeito de ver,
/// guardado no navegador. Sem o cookie, a home agrupa as coleções.
/// </summary>
public static class ListingPreferenceEndpoints
{
    public const string GroupingCookie = "opentube.agrupar";

    public static IEndpointRouteBuilder MapListingPreferenceEndpoints(this IEndpointRouteBuilder rotas)
    {
        rotas.MapPost("/listing/grouping", (HttpContext contexto, [FromForm] string? agrupar, [FromForm] string? destino) =>
        {
            var ligado = agrupar is "1";

            contexto.Response.Cookies.Append(GroupingCookie, ligado ? "1" : "0", new CookieOptions
            {
                MaxAge = TimeSpan.FromDays(365),
                IsEssential = true,
                HttpOnly = true,
                Secure = contexto.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/"
            });

            return Results.Redirect(Retorno.EhLocal(destino) ? destino! : "/");
        });

        return rotas;
    }
}
