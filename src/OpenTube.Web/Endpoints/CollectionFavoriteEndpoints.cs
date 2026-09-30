// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.AspNetCore.Mvc;
using OpenTube.Infrastructure.Playback;
using OpenTube.Infrastructure.Services;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>Estrela da coleção. Só quem entrou marca, e só uma coleção que essa pessoa pode ver.</summary>
public static class CollectionFavoriteEndpoints
{
    public static IEndpointRouteBuilder MapCollectionFavoriteEndpoints(this IEndpointRouteBuilder rotas)
    {
        rotas.MapPost("/collections/{slug}/favorite", async (
            string slug,
            [FromForm] string? destino,
            VideoCatalog catalogo,
            CollectionFavoriteService favoritos,
            CurrentViewer espectadores,
            CancellationToken cancellationToken) =>
        {
            var espectador = await espectadores.GetAsync(cancellationToken);

            // A listagem da coleção já esconde o que a pessoa não pode ver. Sem ela, a estrela
            // não confirma que a coleção existe.
            var lista = await catalogo.PlaylistAsync(espectador, slug, cancellationToken);
            if (lista is null || espectador.UserId is not Guid usuario)
                return Results.NotFound();

            await favoritos.ToggleAsync(usuario, lista.Id, cancellationToken);

            return Results.Redirect(Retorno.EhLocal(destino) ? destino! : "/");
        }).RequireAuthorization();

        return rotas;
    }
}
