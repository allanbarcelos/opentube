// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.AspNetCore.Mvc;
using OpenTube.Infrastructure.Services;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>
/// Opções dos seletores da administração (seletor.js), uma página por vez e filtradas pelo que
/// a pessoa digitou. Só para administradores: listam o acervo inteiro.
/// </summary>
public static class LookupEndpoints
{
    public const string Prefix = "/admin/lookup";

    /// <summary>Vídeos que podem entrar na coleção.</summary>
    public static string VideosForCollection(Guid collectionId) => $"{Prefix}/collections/{collectionId}/videos";

    /// <summary>Coleções, menos a informada.</summary>
    public static string Collections(Guid? except = null) =>
        except is { } id ? $"{Prefix}/collections?except={id}" : $"{Prefix}/collections";

    public static IEndpointRouteBuilder MapLookupEndpoints(this IEndpointRouteBuilder rotas)
    {
        var grupo = rotas.MapGroup(Prefix).RequireAuthorization(Policies.Administrator);

        grupo.MapGet("/collections/{collectionId:guid}/videos", async (
            Guid collectionId,
            [FromQuery] string? q,
            [FromQuery] int? page,
            AdminLookup opcoes,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
            Responder(contexto, await opcoes.VideosForCollectionAsync(collectionId, q, page ?? 1, cancellationToken)));

        grupo.MapGet("/collections", async (
            [FromQuery] string? q,
            [FromQuery] int? page,
            [FromQuery] Guid? except,
            AdminLookup opcoes,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
            Responder(contexto, await opcoes.CollectionsAsync(q, except, page ?? 1, cancellationToken)));

        return rotas;
    }

    /// <summary>A lista muda a cada envio e exclusão: nenhum cache guarda uma resposta antiga.</summary>
    private static IResult Responder(HttpContext contexto, LookupPage pagina)
    {
        contexto.Response.Headers.CacheControl = "no-store";

        return Results.Json(new
        {
            items = pagina.Items.Select(i => new { id = i.Id, label = i.Label, hint = i.Hint }),
            page = pagina.Page,
            hasMore = pagina.HasMore
        });
    }
}
