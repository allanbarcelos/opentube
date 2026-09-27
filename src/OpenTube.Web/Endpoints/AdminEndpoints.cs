using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.Media;
using OpenTube.Infrastructure.Localization;
using OpenTube.Infrastructure.Security;
using OpenTube.Infrastructure.Services;
using OpenTube.Infrastructure.Storage;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>Início de um envio.</summary>
/// <param name="Titulo">Título do vídeo.</param>
/// <param name="Descricao">Descrição opcional.</param>
/// <param name="Arquivo">Nome do arquivo escolhido no navegador.</param>
/// <param name="Tipo">Tipo de mídia informado pelo navegador.</param>
/// <param name="Tamanho">Tamanho em bytes.</param>
public sealed record IniciarEnvio(string? Titulo, string? Descricao, string Arquivo, string? Tipo, long Tamanho, Guid? ColecaoId = null);

/// <summary>Pedido de mais URLs de pedaço.</summary>
/// <param name="UploadId">Envio em andamento.</param>
/// <param name="Primeira">Número do primeiro pedaço.</param>
/// <param name="Quantidade">Quantos pedaços assinar.</param>
public sealed record AssinarPartes(string UploadId, int Primeira, int Quantidade);

/// <summary>Pedaço já enviado.</summary>
/// <param name="Numero">Número do pedaço.</param>
/// <param name="ETag">Etiqueta devolvida pelo storage.</param>
public sealed record ParteEnviada(int Numero, string ETag);

/// <summary>Conclusão do envio.</summary>
/// <param name="UploadId">Envio em andamento.</param>
/// <param name="Partes">Pedaços enviados.</param>
public sealed record ConcluirEnvio(string UploadId, ParteEnviada[] Partes, Guid? ColecaoId = null);

/// <summary>Coleção criada para receber os vídeos de uma pasta.</summary>
/// <param name="Nome">Nome da coleção (por padrão, o da pasta).</param>
/// <param name="Descricao">Descrição opcional.</param>
public sealed record CriarColecaoDoEnvio(string Nome, string? Descricao);

/// <summary>Cancelamento de um envio abandonado.</summary>
/// <param name="UploadId">Envio em andamento.</param>
public sealed record CancelarEnvio(string UploadId);

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder rotas)
    {
        MapEnvio(rotas);
        MapGerenciamento(rotas);

        return rotas;
    }

    private static void MapEnvio(IEndpointRouteBuilder rotas)
    {
        // O envio é conduzido por JavaScript, então a proteção contra falsificação precisa
        // ser conferida à mão: a validação automática só cobre formulários comuns.
        var grupo = rotas.MapGroup("/api/admin/uploads")
            .RequireAuthorization(Policies.Administrator)
            .AddEndpointFilter(ValidarAntifalsificacaoAsync);

        grupo.MapPost("/start", async (
            [FromBody] IniciarEnvio pedido,
            VideoUploadService envios,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var admin = ViewerContext.From(contexto.User);

            UploadTicket bilhete;
            try
            {
                bilhete = await envios.StartAsync(
                    pedido.Titulo, pedido.Descricao, pedido.Arquivo, pedido.Tipo, pedido.Tamanho,
                    admin.UserId!.Value, pedido.ColecaoId, cancellationToken);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                return Results.BadRequest(new { erro = LocalText.Get(e.Message) });
            }

            return Results.Ok(new
            {
                videoId = bilhete.VideoId,
                uploadId = bilhete.UploadId,
                tamanhoDoPedaco = bilhete.PartSizeBytes,
                totalDePedacos = bilhete.PartCount,
                partes = bilhete.Parts.Select(p => new { numero = p.PartNumber, url = p.Url })
            });
        });

        grupo.MapPost("/{videoId:guid}/parts", async (
            Guid videoId,
            [FromBody] AssinarPartes pedido,
            VideoUploadService envios,
            CancellationToken cancellationToken) =>
        {
            var partes = await envios.SignMorePartsAsync(
                videoId, pedido.UploadId, pedido.Primeira, pedido.Quantidade, cancellationToken);

            return Results.Ok(new { partes = partes.Select(p => new { numero = p.PartNumber, url = p.Url }) });
        });

        grupo.MapPost("/{videoId:guid}/complete", async (
            Guid videoId,
            [FromBody] ConcluirEnvio pedido,
            VideoUploadService envios,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            Video video;
            try
            {
                video = await envios.CompleteAsync(
                    videoId,
                    pedido.UploadId,
                    pedido.Partes.Select(p => new CompletedPart(p.Numero, p.ETag)),
                    pedido.ColecaoId,
                    cancellationToken);
            }
            catch (InvalidOperationException e)
            {
                return Results.BadRequest(new { erro = LocalText.Get(e.Message) });
            }

            await contexto.RegistrarAsync(
                AuditActions.VideoEnviado, AuditEntities.Video, video.Id,
                LocalText.Format("Video '{0}' uploaded", video.Title), cancellationToken);

            return Results.Ok(new { destino = $"/admin/videos/{video.Id}" });
        });

        // Envio de uma pasta: a coleção nasce antes do primeiro arquivo, e cada vídeo entra
        // nela ao terminar de subir.
        grupo.MapPost("/collection", async (
            [FromBody] CriarColecaoDoEnvio pedido,
            CollectionService colecoes,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var admin = ViewerContext.From(contexto.User);

            try
            {
                var nome = (pedido.Nome ?? string.Empty).Trim();
                if (nome.Length > UploadNames.MaxCollectionNameLength)
                    nome = nome[..UploadNames.MaxCollectionNameLength].TrimEnd();

                var colecao = await colecoes.CreateAsync(nome, pedido.Descricao, admin.UserId!.Value, cancellationToken);

                await contexto.RegistrarAsync(
                    AuditActions.ColecaoCriada, AuditEntities.Colecao, colecao.Id,
                    LocalText.Format("Collection '{0}' created", colecao.Name), cancellationToken);

                return Results.Ok(new { colecaoId = colecao.Id, nome = colecao.Name, destino = $"/admin/collections/{colecao.Id}" });
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new { erro = LocalText.Get("Enter a name for the collection.") });
            }
        });

        grupo.MapPost("/{videoId:guid}/cancel", async (
            Guid videoId,
            [FromBody] CancelarEnvio pedido,
            VideoUploadService envios,
            CancellationToken cancellationToken) =>
        {
            await envios.AbortAsync(videoId, pedido.UploadId, cancellationToken);

            return Results.NoContent();
        });
    }

    private static void MapGerenciamento(IEndpointRouteBuilder rotas)
    {
        var grupo = rotas.MapGroup("/admin/videos/{videoId:guid}")
            .RequireAuthorization(Policies.Administrator);

        grupo.MapPost("/save", async (
            Guid videoId,
            [FromForm] string titulo,
            [FromForm] string? descricao,
            [FromForm] string? etiquetas,
            [FromForm] int visibilidade,
            AdminVideoService admin,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await admin.UpdateAsync(
                    videoId, titulo, descricao,
                    SepararEtiquetas(etiquetas),
                    (VideoVisibility)visibilidade,
                    cancellationToken);

                await contexto.RegistrarAsync(
                    AuditActions.VideoAlterado, AuditEntities.Video, videoId,
                    LocalText.Format("Video '{0}' saved with visibility {1}", titulo, Visibilidade((VideoVisibility)visibilidade)),
                    cancellationToken);

                return Results.Redirect($"/admin/videos/{videoId}?salvo=1");
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                return Results.Redirect($"/admin/videos/{videoId}?erro={Uri.EscapeDataString(LocalText.Get(e.Message))}");
            }
        });

        grupo.MapPost("/delete", async (
            Guid videoId, AdminVideoService admin, HttpContext contexto, CancellationToken cancellationToken) =>
        {
            await admin.DeleteAsync(videoId, cancellationToken);

            await contexto.RegistrarAsync(
                AuditActions.VideoExcluido, AuditEntities.Video, videoId, LocalText.Get("Video deleted."), cancellationToken);

            return Results.Redirect("/admin?excluido=1");
        });

        grupo.MapPost("/restore", async (
            Guid videoId, AdminVideoService admin, HttpContext contexto, CancellationToken cancellationToken) =>
        {
            await admin.RestoreAsync(videoId, cancellationToken);

            await contexto.RegistrarAsync(
                AuditActions.VideoRestaurado, AuditEntities.Video, videoId, LocalText.Get("Video restored"), cancellationToken);

            return Results.Redirect($"/admin/videos/{videoId}?restaurado=1");
        });

        grupo.MapPost("/reprocess", async (
            Guid videoId, AdminVideoService admin, HttpContext contexto, CancellationToken cancellationToken) =>
        {
            try
            {
                await admin.RequeueAsync(videoId, cancellationToken);

                await contexto.RegistrarAsync(
                    AuditActions.VideoReprocessado, AuditEntities.Video, videoId,
                    LocalText.Get("Video put back in the processing queue"), cancellationToken);

                return Results.Redirect($"/admin/videos/{videoId}?enfileirado=1");
            }
            catch (InvalidOperationException e)
            {
                return Results.Redirect($"/admin/videos/{videoId}?erro={Uri.EscapeDataString(LocalText.Get(e.Message))}");
            }
        });
    }

    private static string Visibilidade(VideoVisibility visibilidade) => LocalText.Get(visibilidade switch
    {
        VideoVisibility.Public => "Public",
        VideoVisibility.Restricted => "Restricted",
        _ => "Private"
    });

    /// <summary>Divide a lista de etiquetas digitada pelo administrador.</summary>
    public static IEnumerable<string> SepararEtiquetas(string? texto) =>
        string.IsNullOrWhiteSpace(texto)
            ? []
            : texto.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async ValueTask<object?> ValidarAntifalsificacaoAsync(
        EndpointFilterInvocationContext contexto,
        EndpointFilterDelegate proximo)
    {
        var antifalsificacao = contexto.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();

        try
        {
            await antifalsificacao.ValidateRequestAsync(contexto.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest(new { erro = LocalText.Get("Request is missing the security credential.") });
        }

        return await proximo(contexto);
    }
}
