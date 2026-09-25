using Microsoft.AspNetCore.Mvc;
using OpenTube.Infrastructure.Security;
using OpenTube.Infrastructure.Services;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>Administração das coleções, por formulários comuns.</summary>
public static class CollectionEndpoints
{
    public static IEndpointRouteBuilder MapCollectionEndpoints(this IEndpointRouteBuilder rotas)
    {
        var grupo = rotas.MapGroup("/admin/colecoes").RequireAuthorization(Policies.Administrator);

        grupo.MapPost("/criar", async (
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
                    $"Coleção '{colecao.Name}' criada", cancellationToken);

                return Results.Redirect($"/admin/colecoes/{colecao.Id}?criada=1");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Results.Redirect($"/admin/colecoes?erro={Uri.EscapeDataString(e.Message)}");
            }
        });

        grupo.MapPost("/{collectionId:guid}/salvar", async (
            Guid collectionId,
            [FromForm] string nome,
            [FromForm] string? descricao,
            CollectionService colecoes,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await colecoes.RenameAsync(collectionId, nome, descricao, cancellationToken);

                return Results.Redirect($"/admin/colecoes/{collectionId}?salva=1");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Results.Redirect($"/admin/colecoes/{collectionId}?erro={Uri.EscapeDataString(e.Message)}");
            }
        });

        grupo.MapPost("/{collectionId:guid}/videos/adicionar", async (
            Guid collectionId,
            [FromForm] Guid videoId,
            CollectionService colecoes,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await colecoes.AddVideoAsync(collectionId, videoId, cancellationToken);

                return Results.Redirect($"/admin/colecoes/{collectionId}?adicionado=1");
            }
            catch (InvalidOperationException e)
            {
                return Results.Redirect($"/admin/colecoes/{collectionId}?erro={Uri.EscapeDataString(e.Message)}");
            }
        });

        grupo.MapPost("/{collectionId:guid}/videos/remover", async (
            Guid collectionId,
            [FromForm] Guid videoId,
            CollectionService colecoes,
            CancellationToken cancellationToken) =>
        {
            await colecoes.RemoveVideoAsync(collectionId, videoId, cancellationToken);

            return Results.Redirect($"/admin/colecoes/{collectionId}?removido=1");
        });

        grupo.MapPost("/{collectionId:guid}/excluir", async (
            Guid collectionId,
            CollectionService colecoes,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            await colecoes.DeleteAsync(collectionId, cancellationToken);

            await contexto.RegistrarAsync(
                AuditActions.ColecaoExcluida, AuditEntities.Colecao, collectionId, "Coleção excluída", cancellationToken);

            return Results.Redirect("/admin/colecoes?excluida=1");
        });

        grupo.MapPost("/{collectionId:guid}/restaurar", async (
            Guid collectionId,
            CollectionService colecoes,
            CancellationToken cancellationToken) =>
        {
            await colecoes.RestoreAsync(collectionId, cancellationToken);

            return Results.Redirect($"/admin/colecoes/{collectionId}?restaurada=1");
        });

        return rotas;
    }
}
