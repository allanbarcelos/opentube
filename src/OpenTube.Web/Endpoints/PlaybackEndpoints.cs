// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Access;
using OpenTube.Infrastructure.Playback;
using OpenTube.Infrastructure.Security;
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
            PrivacyHasher privacidade,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var espectador = await espectadores.GetAsync(cancellationToken);

            var resultado = await playback.GetMasterAsync(
                videoId,
                espectador,
                versao => $"/api/videos/{videoId}/renditions/{versao}.m3u8",
                privacidade.HashIp(contexto.Connection.RemoteIpAddress?.ToString()),
                cancellationToken);

            // O bilhete deixa o restante desta reprodução passar, mesmo que ela tenha
            // consumido a última visualização da concessão.
            if (resultado.Ticket is { } bilhete)
                CurrentViewer.AppendTicket(contexto, bilhete);

            return Responder(resultado, MediaTypes.HlsPlaylist);
        });

        grupo.MapGet("/renditions/{rendition}.m3u8", async (
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

        grupo.MapGet("/thumbnail", async (
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
            // Reproduções simultâneas demais merecem resposta própria: aqui o acesso existe,
            // e esconder o motivo deixaria a pessoa sem saber o que fazer.
            AccessReason.TooManyStreams => Results.StatusCode(StatusCodes.Status429TooManyRequests),
            _ => Results.NotFound()
        };
    }
}
