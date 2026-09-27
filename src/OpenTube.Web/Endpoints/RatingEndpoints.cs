// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.AspNetCore.Mvc;
using OpenTube.Infrastructure.Localization;
using OpenTube.Infrastructure.Services;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>Avaliação de utilidade, pela página do vídeo.</summary>
public static class RatingEndpoints
{
    public static IEndpointRouteBuilder MapRatingEndpoints(this IEndpointRouteBuilder rotas)
    {
        // O avaliacao.js envia pelo fetch, para não interromper o vídeo, e recebe JSON; sem
        // script, o formulário comum volta para a página do vídeo.
        rotas.MapPost("/videos/{videoId:guid}/rating", async (
            Guid videoId,
            [FromForm] int nota,
            [FromForm] string? destino,
            VideoRatingService avaliacoes,
            CurrentViewer espectadores,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var querJson = contexto.Request.Headers.Accept.Any(a => a?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);
            var espectador = await espectadores.GetAsync(cancellationToken);

            try
            {
                var gravada = await avaliacoes.RateAsync(videoId, espectador, nota, cancellationToken);

                return querJson
                    ? Results.Ok(new { nota = gravada })
                    : Results.Redirect(Retorno.Para(destino, "avaliado=1#avaliacao"));
            }
            catch (InvalidOperationException e)
            {
                var mensagem = LocalText.Get(e.Message);
                return querJson
                    ? Results.BadRequest(new { erro = mensagem })
                    : Results.Redirect(Retorno.Para(destino, "erro-avaliacao=" + Uri.EscapeDataString(mensagem) + "#avaliacao"));
            }
        }).RequireAuthorization();

        return rotas;
    }
}
