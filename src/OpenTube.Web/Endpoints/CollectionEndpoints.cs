using Microsoft.AspNetCore.Mvc;
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
            CollectionService colecoes,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await colecoes.AddVideoAsync(collectionId, videoId, cancellationToken);

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

        grupo.MapPost("/{collectionId:guid}/delete", async (
            Guid collectionId,
            CollectionService colecoes,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            await colecoes.DeleteAsync(collectionId, cancellationToken);

            await contexto.RegistrarAsync(
                AuditActions.ColecaoExcluida, AuditEntities.Colecao, collectionId, LocalText.Get("Collection deleted."), cancellationToken);

            return Results.Redirect("/admin/collections?excluida=1");
        });

        grupo.MapPost("/{collectionId:guid}/restore", async (
            Guid collectionId,
            CollectionService colecoes,
            CancellationToken cancellationToken) =>
        {
            await colecoes.RestoreAsync(collectionId, cancellationToken);

            return Results.Redirect($"/admin/collections/{collectionId}?restaurada=1");
        });

        return rotas;
    }
}
