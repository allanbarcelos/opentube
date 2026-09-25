using OpenTube.Domain.Access;
using OpenTube.Infrastructure.Playback;
using OpenTube.Infrastructure.Storage;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>
/// Entrega das playlists. Cada pedido passa pela política de acesso; os segmentos em si saem
/// direto do storage por endereço assinado de vida curta, para que a aplicação decida sem
/// precisar transportar os bytes.
/// </summary>
public static class PlaybackEndpoints
{
    public static IEndpointRouteBuilder MapPlaybackEndpoints(this IEndpointRouteBuilder rotas)
    {
        var grupo = rotas.MapGroup("/api/videos/{videoId:guid}");

        grupo.MapGet("/master.m3u8", async (
            Guid videoId,
            PlaybackService playback,
            CurrentViewer espectadores,
            CancellationToken cancellationToken) =>
        {
            var espectador = await espectadores.GetAsync(cancellationToken);

            var resultado = await playback.GetMasterAsync(
                videoId,
                espectador,
                versao => $"/api/videos/{videoId}/versoes/{versao}.m3u8",
                cancellationToken);

            return Responder(resultado, MediaTypes.HlsPlaylist);
        });

        grupo.MapGet("/versoes/{rendition}.m3u8", async (
            Guid videoId,
            string rendition,
            PlaybackService playback,
            CurrentViewer espectadores,
            CancellationToken cancellationToken) =>
        {
            var espectador = await espectadores.GetAsync(cancellationToken);
            var resultado = await playback.GetRenditionAsync(videoId, rendition, espectador, cancellationToken);

            return Responder(resultado, MediaTypes.HlsPlaylist);
        });

        grupo.MapGet("/miniatura", async (
            Guid videoId,
            PlaybackService playback,
            CurrentViewer espectadores,
            CancellationToken cancellationToken) =>
        {
            var endereco = await playback.GetThumbnailUrlAsync(
                videoId, await espectadores.GetAsync(cancellationToken), cancellationToken);

            return endereco is null ? Results.NotFound() : Results.Redirect(endereco);
        });

        return rotas;
    }

    private static IResult Responder(PlaybackResult resultado, string tipo)
    {
        if (resultado.Allowed)
            return Results.Text(resultado.Content!, tipo);

        // Vídeo privado e vídeo inexistente respondem igual: a diferença entre "não existe" e
        // "existe mas você não pode ver" já é informação sobre o acervo.
        return resultado.Reason switch
        {
            AccessReason.VideoNotReady => Results.StatusCode(StatusCodes.Status409Conflict),
            _ => Results.NotFound()
        };
    }
}
