using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using OpenTube.Domain.Enums;
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
public sealed record IniciarEnvio(string Titulo, string? Descricao, string Arquivo, string? Tipo, long Tamanho);

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
public sealed record ConcluirEnvio(string UploadId, ParteEnviada[] Partes);

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
        var grupo = rotas.MapGroup("/api/admin/envios")
            .RequireAuthorization(Policies.Administrator)
            .AddEndpointFilter(ValidarAntifalsificacaoAsync);

        grupo.MapPost("/iniciar", async (
            [FromBody] IniciarEnvio pedido,
            VideoUploadService envios,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var admin = ViewerContext.From(contexto.User);

            var bilhete = await envios.StartAsync(
                pedido.Titulo, pedido.Descricao, pedido.Arquivo, pedido.Tipo, pedido.Tamanho,
                admin.UserId!.Value, cancellationToken);

            return Results.Ok(new
            {
                videoId = bilhete.VideoId,
                uploadId = bilhete.UploadId,
                tamanhoDoPedaco = bilhete.PartSizeBytes,
                totalDePedacos = bilhete.PartCount,
                partes = bilhete.Parts.Select(p => new { numero = p.PartNumber, url = p.Url })
            });
        });

        grupo.MapPost("/{videoId:guid}/partes", async (
            Guid videoId,
            [FromBody] AssinarPartes pedido,
            VideoUploadService envios,
            CancellationToken cancellationToken) =>
        {
            var partes = await envios.SignMorePartsAsync(
                videoId, pedido.UploadId, pedido.Primeira, pedido.Quantidade, cancellationToken);

            return Results.Ok(new { partes = partes.Select(p => new { numero = p.PartNumber, url = p.Url }) });
        });

        grupo.MapPost("/{videoId:guid}/concluir", async (
            Guid videoId,
            [FromBody] ConcluirEnvio pedido,
            VideoUploadService envios,
            CancellationToken cancellationToken) =>
        {
            var video = await envios.CompleteAsync(
                videoId,
                pedido.UploadId,
                pedido.Partes.Select(p => new CompletedPart(p.Numero, p.ETag)),
                cancellationToken);

            return Results.Ok(new { destino = $"/admin/videos/{video.Id}" });
        });

        grupo.MapPost("/{videoId:guid}/cancelar", async (
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

        grupo.MapPost("/salvar", async (
            Guid videoId,
            [FromForm] string titulo,
            [FromForm] string? descricao,
            [FromForm] string? etiquetas,
            [FromForm] int visibilidade,
            AdminVideoService admin,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await admin.UpdateAsync(
                    videoId, titulo, descricao,
                    SepararEtiquetas(etiquetas),
                    (VideoVisibility)visibilidade,
                    cancellationToken);

                return Results.Redirect($"/admin/videos/{videoId}?salvo=1");
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                return Results.Redirect($"/admin/videos/{videoId}?erro={Uri.EscapeDataString(e.Message)}");
            }
        });

        grupo.MapPost("/excluir", async (Guid videoId, AdminVideoService admin, CancellationToken cancellationToken) =>
        {
            await admin.DeleteAsync(videoId, cancellationToken);

            return Results.Redirect("/admin?excluido=1");
        });

        grupo.MapPost("/restaurar", async (Guid videoId, AdminVideoService admin, CancellationToken cancellationToken) =>
        {
            await admin.RestoreAsync(videoId, cancellationToken);

            return Results.Redirect($"/admin/videos/{videoId}?restaurado=1");
        });

        grupo.MapPost("/reprocessar", async (Guid videoId, AdminVideoService admin, CancellationToken cancellationToken) =>
        {
            try
            {
                await admin.RequeueAsync(videoId, cancellationToken);

                return Results.Redirect($"/admin/videos/{videoId}?enfileirado=1");
            }
            catch (InvalidOperationException e)
            {
                return Results.Redirect($"/admin/videos/{videoId}?erro={Uri.EscapeDataString(e.Message)}");
            }
        });
    }

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
            return Results.BadRequest(new { erro = "Pedido sem a credencial de segurança." });
        }

        return await proximo(contexto);
    }
}
