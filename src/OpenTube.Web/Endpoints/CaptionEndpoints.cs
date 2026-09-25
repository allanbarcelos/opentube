using Microsoft.AspNetCore.Mvc;
using OpenTube.Infrastructure.Playback;
using OpenTube.Infrastructure.Security;
using OpenTube.Infrastructure.Services;
using OpenTube.Infrastructure.Storage;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>Legendas: envio pela administração e entrega a quem tem acesso ao vídeo.</summary>
public static class CaptionEndpoints
{
    public static IEndpointRouteBuilder MapCaptionEndpoints(this IEndpointRouteBuilder rotas)
    {
        var administracao = rotas.MapGroup("/admin/videos/{videoId:guid}/legendas")
            .RequireAuthorization(Policies.Administrator);

        administracao.MapPost("/", async (
            Guid videoId,
            [FromForm] IFormFile arquivo,
            [FromForm] string idioma,
            [FromForm] string? rotulo,
            CaptionService legendas,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (arquivo is null || arquivo.Length == 0)
                    throw new InvalidOperationException("Escolha um arquivo de legenda.");

                if (arquivo.Length > CaptionService.MaxSizeBytes)
                    throw new InvalidOperationException("O arquivo de legenda é grande demais.");

                using var leitor = new StreamReader(arquivo.OpenReadStream());
                var conteudo = await leitor.ReadToEndAsync(cancellationToken);

                await legendas.UploadAsync(videoId, idioma, rotulo, conteudo, cancellationToken);

                await contexto.RegistrarAsync(
                    AuditActions.VideoAlterado, AuditEntities.Video, videoId,
                    $"Legenda {idioma} enviada", cancellationToken);

                return Results.Redirect($"/admin/videos/{videoId}?legenda=1");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Results.Redirect($"/admin/videos/{videoId}?erro={Uri.EscapeDataString(e.Message)}");
            }
        });

        administracao.MapPost("/{assetId:guid}/excluir", async (
            Guid videoId,
            Guid assetId,
            CaptionService legendas,
            CancellationToken cancellationToken) =>
        {
            await legendas.DeleteAsync(assetId, cancellationToken);

            return Results.Redirect($"/admin/videos/{videoId}?legendaRemovida=1");
        });

        administracao.MapPost("/transcrever", async (
            Guid videoId,
            CaptionService legendas,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await legendas.RequestTranscriptionAsync(videoId, cancellationToken);

                await contexto.RegistrarAsync(
                    AuditActions.VideoReprocessado, AuditEntities.Video, videoId,
                    "Transcrição automática solicitada", cancellationToken);

                return Results.Redirect($"/admin/videos/{videoId}?transcrevendo=1");
            }
            catch (InvalidOperationException e)
            {
                return Results.Redirect($"/admin/videos/{videoId}?erro={Uri.EscapeDataString(e.Message)}");
            }
        });

        // A legenda segue a mesma regra de acesso do vídeo: ela é parte do conteúdo, e o
        // texto falado costuma revelar tanto quanto a imagem.
        rotas.MapGet("/api/videos/{videoId:guid}/legendas/{assetId:guid}.vtt", async (
            Guid videoId,
            Guid assetId,
            CaptionService legendas,
            PlaybackService playback,
            CurrentViewer espectadores,
            CancellationToken cancellationToken) =>
        {
            var espectador = await espectadores.GetAsync(cancellationToken);

            if (!await playback.CanReceiveMediaAsync(videoId, espectador, objectKey: null, cancellationToken))
                return Results.NotFound();

            var legenda = await legendas.ReadAsync(assetId, cancellationToken);

            return legenda is null || legenda.Value.Asset.VideoId != videoId
                ? Results.NotFound()
                : Results.Text(legenda.Value.Content, MediaTypes.WebVtt);
        });

        return rotas;
    }
}
