// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.AspNetCore.Mvc;
using OpenTube.Infrastructure.Playback;
using OpenTube.Infrastructure.Services;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>Estrela do vídeo. Só quem entrou marca, e só um vídeo que essa pessoa pode ver.</summary>
public static class VideoFavoriteEndpoints
{
    public static IEndpointRouteBuilder MapVideoFavoriteEndpoints(this IEndpointRouteBuilder rotas)
    {
        rotas.MapPost("/videos/{slug}/favorite", async (
            string slug,
            [FromForm] string? destino,
            VideoCatalog catalogo,
            VideoFavoriteService favoritos,
            CurrentViewer espectadores,
            CancellationToken cancellationToken) =>
        {
            var espectador = await espectadores.GetAsync(cancellationToken);
            var video = await catalogo.FindBySlugAsync(espectador, slug, cancellationToken);

            if (video is null || espectador.UserId is not Guid usuario)
                return Results.NotFound();

            await favoritos.ToggleAsync(usuario, video.Id, cancellationToken);

            return Results.Redirect(Retorno.EhLocal(destino) ? destino! : "/");
        }).RequireAuthorization();

        return rotas;
    }
}
