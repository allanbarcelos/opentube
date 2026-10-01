// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Access;
using OpenTube.Infrastructure.Playback;
using OpenTube.Infrastructure.Security;
using OpenTube.Infrastructure.Storage;
using OpenTube.Web.Auth;
using OpenTube.Web.Seguranca;

namespace OpenTube.Web.Endpoints;

/// <summary>Pedido do player para começar uma reprodução.</summary>
/// <param name="Video">Vídeo da página.</param>
/// <param name="Token">Token que a página entregou ao player.</param>
public sealed record PedidoDeReproducao(Guid Video, string? Token);

/// <summary>
/// Entrega das playlists. Cada pedido passa pela política de acesso; os segmentos em si saem
/// do storage, autorizados um a um, para que a aplicação decida sem transportar os bytes.
/// </summary>
/// <remarks>
/// Nenhum endereço de vídeo aparece na página nem é legível no DevTools. A página entrega ao
/// player só um token; com ele, o player pede a reprodução e recebe o endereço opaco da playlist
/// principal, que aponta para as versões, que apontam para os pedaços — cada um por um selo
/// cifrado, preso ao vídeo, a quem assiste e a um prazo. Abrir qualquer um deles direto numa aba
/// é recusado.
/// </remarks>
public static class PlaybackEndpoints
{
    /// <summary>Cabeçalho que a hls.js manda: a ela basta um tipo genérico na resposta.</summary>
    public const string PlayerHeader = "X-OpenTube-Player";

    /// <summary>Caminho dos pedaços do vídeo, entregues pelo servidor da frente.</summary>
    public const string SegmentRoute = "/s/";

    private const string Generico = "application/octet-stream";

    public static IEndpointRouteBuilder MapPlaybackEndpoints(this IEndpointRouteBuilder rotas)
    {
        // Não muda nada no servidor: só troca o token da página pelo endereço da reprodução.
        // Fica fora da exigência de token antifalsificação, que o player não tem; quem o protege
        // é a origem do pedido, que o navegador declara e outro site não consegue imitar.
        rotas.MapPost("/api/play", async (
            PedidoDeReproducao pedido,
            PlaybackTokens tokens,
            PlaybackSeals selos,
            CurrentViewer espectadores,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var espectador = await espectadores.GetAsync(cancellationToken);

            if (PedidoDoPlayer.EhAberturaDireta(contexto.Request)
                || !tokens.Validate(pedido.Token, pedido.Video, espectador))
                return ForaDoPlayer();

            contexto.Response.Headers.CacheControl = "no-store";

            return Results.Json(new { src = "/api/m/" + selos.Seal(PlaybackSealKind.Master, pedido.Video, espectador) });
        });

        rotas.MapGet("/api/m/{selo}", async (
            string selo,
            PlaybackService playback,
            PlaybackSeals selos,
            CurrentViewer espectadores,
            PrivacyHasher privacidade,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var espectador = await espectadores.GetAsync(cancellationToken);

            if (PedidoDoPlayer.EhAberturaDireta(contexto.Request)
                || selos.Open(selo, PlaybackSealKind.Master, espectador) is not { } aberto)
                return ForaDoPlayer();

            var resultado = await playback.GetMasterAsync(
                aberto.VideoId,
                espectador,
                versao => "/api/p/" + selos.Seal(PlaybackSealKind.Rendition, aberto.VideoId, espectador, versao),
                privacidade.HashIp(contexto.Connection.RemoteIpAddress?.ToString()),
                cancellationToken);

            // O bilhete deixa o restante desta reprodução passar, mesmo que ela tenha
            // consumido a última visualização da concessão.
            if (resultado.Ticket is { } bilhete)
                CurrentViewer.AppendTicket(contexto, bilhete);

            return Responder(resultado, contexto);
        });

        rotas.MapGet("/api/p/{selo}", async (
            string selo,
            PlaybackService playback,
            PlaybackSeals selos,
            CurrentViewer espectadores,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var espectador = await espectadores.GetAsync(cancellationToken);

            if (PedidoDoPlayer.EhAberturaDireta(contexto.Request)
                || selos.Open(selo, PlaybackSealKind.Rendition, espectador) is not { } aberto)
                return ForaDoPlayer();

            var resultado = await playback.GetRenditionAsync(
                aberto.VideoId, aberto.Path, espectador, cancellationToken,
                segmentUrl: arquivo => SegmentRoute + selos.Seal(PlaybackSealKind.Segment, aberto.VideoId, espectador, arquivo));

            return Responder(resultado, contexto);
        });

        rotas.MapGet("/api/videos/{videoId:guid}/thumbnail", async (
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

    private static IResult ForaDoPlayer() =>
        Results.Text("Open the video on its page.", "text/plain", statusCode: StatusCodes.Status403Forbidden);

    private static IResult Responder(PlaybackResult resultado, HttpContext contexto)
    {
        if (resultado.Allowed)
        {
            contexto.Response.Headers.CacheControl = "no-store";

            // O player nativo do Safari precisa do tipo HLS para reconhecer a playlist, já que o
            // endereço não tem extensão; à hls.js basta o genérico, que não diz o que é.
            var tipo = contexto.Request.Headers.ContainsKey(PlayerHeader) ? Generico : MediaTypes.HlsPlaylist;

            return Results.Text(resultado.Content!, tipo);
        }

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
