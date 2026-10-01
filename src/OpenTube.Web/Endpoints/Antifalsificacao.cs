// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.AspNetCore.Antiforgery;
using OpenTube.Infrastructure.Localization;

namespace OpenTube.Web.Endpoints;

/// <summary>
/// Proteção contra falsificação para todo pedido que altera alguma coisa. A validação automática
/// do ASP.NET só barra endereços que leem campos de formulário; um POST sem campos — excluir um
/// vídeo, reprocessar, sair — passaria sem token nenhum, protegido só pelo SameSite do cookie.
/// </summary>
public static class Antifalsificacao
{
    public static TBuilder ExigirAntifalsificacao<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(ValidarAsync);

    private static async ValueTask<object?> ValidarAsync(EndpointFilterInvocationContext contexto, EndpointFilterDelegate proximo)
    {
        var http = contexto.HttpContext;

        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)
            || HttpMethods.IsOptions(http.Request.Method) || HttpMethods.IsTrace(http.Request.Method))
            return await proximo(contexto);

        try
        {
            await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
        }
        catch (AntiforgeryValidationException)
        {
            // Formulário comum recebe o mesmo 400 da validação automática; os envios por script
            // (upload, editor de legendas) mostram a mensagem que vem no JSON.
            return http.Request.HasFormContentType
                ? Results.BadRequest()
                : Results.BadRequest(new { erro = LocalText.Get("The page expired. Reload it and try again.") });
        }

        return await proximo(contexto);
    }
}
