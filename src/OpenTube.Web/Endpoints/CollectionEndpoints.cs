// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.AspNetCore.Mvc;
using OpenTube.Infrastructure.Branding;
using OpenTube.Infrastructure.Localization;
using OpenTube.Infrastructure.Security;
using OpenTube.Infrastructure.Services;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>Administração das coleções, por formulários comuns.</summary>
public static class CollectionEndpoints
{
    public static IEndpointRouteBuilder MapCollectionEndpoints(this IEndpointRouteBuilder rotas)
    {
        rotas.MapGet("/api/collections/{collectionId:guid}/thumbnail", async (
            Guid collectionId,
            CollectionThumbnailService miniaturas,
            CurrentViewer espectadores,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var endereco = await miniaturas.GetUrlAsync(
                collectionId, await espectadores.GetAsync(cancellationToken), cancellationToken);

            if (endereco is null)
                return Results.NotFound();

            // O endereço da página leva a versão da imagem. A resposta daqui não pode ficar
            // em cache: ela é o que decide se esta pessoa ainda pode ver a capa.
            contexto.Response.Headers.CacheControl = "no-store";

            return Results.Redirect(endereco);
        });

        var grupo = rotas.MapGroup("/admin/collections").RequireAuthorization(Policies.Administrator);

        grupo.MapPost("/create", async (
            [FromForm] string nome,
            [FromForm] string? descricao,
            CollectionService colecoes,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var admin = ViewerContext.From(contexto.User);

            try
            {
                var colecao = await colecoes.CreateAsync(nome, descricao, admin.UserId!.Value, cancellationToken);

                await contexto.RegistrarAsync(
                    AuditActions.ColecaoCriada, AuditEntities.Colecao, colecao.Id,
                    LocalText.Format("Collection '{0}' created", colecao.Name), cancellationToken);

                return Results.Redirect($"/admin/collections/{colecao.Id}?criada=1");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Results.Redirect($"/admin/collections?erro={Uri.EscapeDataString(LocalText.Get(e.Message))}");
            }
        });

        grupo.MapPost("/{collectionId:guid}/save", async (
            Guid collectionId,
            [FromForm] string nome,
            [FromForm] string? descricao,
            CollectionService colecoes,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await colecoes.RenameAsync(collectionId, nome, descricao, cancellationToken);

                return Results.Redirect($"/admin/collections/{collectionId}?salva=1");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Results.Redirect($"/admin/collections/{collectionId}?erro={Uri.EscapeDataString(LocalText.Get(e.Message))}");
            }
        });

        grupo.MapPost("/{collectionId:guid}/videos/add", async (
            Guid collectionId,
            [FromForm] Guid videoId,
            [FromForm] bool? confirmar,
            CollectionService colecoes,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var resultado = await colecoes.AddVideoAsync(collectionId, videoId, confirmar == true, cancellationToken);

                if (resultado.NeedsConfirmation)
                    return Results.Redirect($"/admin/collections/{collectionId}?mover={videoId}");

                if (resultado.Moved)
                    return Results.Redirect($"/admin/collections/{collectionId}?movido=1");

                return Results.Redirect($"/admin/collections/{collectionId}?adicionado=1");
            }
            catch (InvalidOperationException e)
            {
                return Results.Redirect($"/admin/collections/{collectionId}?erro={Uri.EscapeDataString(LocalText.Get(e.Message))}");
            }
        });

        grupo.MapPost("/{collectionId:guid}/videos/remove", async (
            Guid collectionId,
            [FromForm] Guid videoId,
            CollectionService colecoes,
            CancellationToken cancellationToken) =>
        {
            await colecoes.RemoveVideoAsync(collectionId, videoId, cancellationToken);

            return Results.Redirect($"/admin/collections/{collectionId}?removido=1");
        });

        grupo.MapPost("/{collectionId:guid}/thumbnail", async (
            Guid collectionId,
            IFormFile? arquivo,
            CollectionThumbnailService miniaturas,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (arquivo is null || arquivo.Length == 0)
                    throw new ArgumentException("Choose an image.");

                if (arquivo.Length > CollectionThumbnailProcessor.MaxUploadBytes)
                    throw new ArgumentException("The image is larger than 5 MB.");

                using var memoria = new MemoryStream((int)arquivo.Length);
                await arquivo.CopyToAsync(memoria, cancellationToken);

                await miniaturas.SetAsync(collectionId, memoria.ToArray(), cancellationToken);

                await contexto.RegistrarAsync(
                    AuditActions.ColecaoAlterada, AuditEntities.Colecao, collectionId,
                    LocalText.Get("Collection thumbnail set"), cancellationToken);

                return Results.Redirect($"/admin/collections/{collectionId}?capa=1");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Results.Redirect($"/admin/collections/{collectionId}?erro={Uri.EscapeDataString(LocalText.Get(e.Message))}");
            }
        });

        grupo.MapPost("/{collectionId:guid}/thumbnail/remove", async (
            Guid collectionId,
            CollectionThumbnailService miniaturas,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (await miniaturas.RemoveAsync(collectionId, cancellationToken))
                {
                    await contexto.RegistrarAsync(
                        AuditActions.ColecaoAlterada, AuditEntities.Colecao, collectionId,
                        LocalText.Get("Collection thumbnail removed"), cancellationToken);
                }

                return Results.Redirect($"/admin/collections/{collectionId}?semcapa=1");
            }
            catch (InvalidOperationException e)
            {
                return Results.Redirect($"/admin/collections/{collectionId}?erro={Uri.EscapeDataString(LocalText.Get(e.Message))}");
            }
        });

        grupo.MapPost("/{collectionId:guid}/delete", async (
            Guid collectionId,
            [FromForm] string? videos,
            [FromForm] string? destino,
            CollectionService colecoes,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var escolha = videos switch
                {
                    "desvincular" => VideosDaColecao.Desvincular,
                    "excluir" => VideosDaColecao.Excluir,
                    "mover" => VideosDaColecao.Mover,
                    _ => throw new InvalidOperationException("Choose what happens to the videos.")
                };

                // A opção "Escolha uma coleção…" manda destino vazio. Guid? rejeita "" com 400
                // antes do método rodar, então o campo entra como texto.
                Guid? destinoId = null;
                if (!string.IsNullOrWhiteSpace(destino))
                {
                    if (!Guid.TryParse(destino, out var id))
                        throw new InvalidOperationException("Choose another collection.");

                    destinoId = id;
                }

                var exclusao = await colecoes.DeleteAsync(collectionId, escolha, destinoId, cancellationToken);

                var resumo = exclusao.Videos switch
                {
                    VideosDaColecao.Excluir => LocalText.Format("Collection '{0}' deleted. Its videos were deleted.", exclusao.Name),
                    VideosDaColecao.Mover => LocalText.Format("Collection '{0}' deleted. Its videos were moved to '{1}'.", exclusao.Name, exclusao.DestinationName),
                    _ => LocalText.Format("Collection '{0}' deleted. Its videos stayed in the library.", exclusao.Name)
                };

                await contexto.RegistrarAsync(
                    AuditActions.ColecaoExcluida, AuditEntities.Colecao, collectionId, resumo, cancellationToken);

                foreach (var videoId in exclusao.DeletedVideoIds)
                {
                    await contexto.RegistrarAsync(
                        AuditActions.VideoExcluido, AuditEntities.Video, videoId, LocalText.Get("Video deleted."), cancellationToken);
                }

                var aviso = exclusao.Videos switch
                {
                    VideosDaColecao.Excluir => "excluir",
                    VideosDaColecao.Mover when !string.IsNullOrWhiteSpace(exclusao.DestinationName) => "mover",
                    _ => "desvincular"
                };

                var para = aviso == "mover"
                    ? $"&para={Uri.EscapeDataString(exclusao.DestinationName!)}"
                    : "";

                return Results.Redirect($"/admin/collections?excluida={aviso}{para}");
            }
            catch (InvalidOperationException e)
            {
                return Results.Redirect($"/admin/collections/{collectionId}?erro={Uri.EscapeDataString(LocalText.Get(e.Message))}");
            }
        });

        return rotas;
    }
}
