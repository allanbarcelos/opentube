// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.Extensions.Options;
using OpenTube.Domain.Access;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Playback;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>
/// Autorização de cada segmento de vídeo, para quando o servidor da frente entrega os
/// arquivos. A aplicação decide e o servidor transporta: assim a revogação vale no segmento
/// seguinte, e a aplicação não gasta banda nem memória com bytes de vídeo.
/// </summary>
public static class SegmentAuthorizationEndpoints
{
    /// <summary>Cabeçalhos em que os servidores comuns informam o endereço original.</summary>
    private static readonly string[] CabecalhosDeOrigem =
        ["X-Forwarded-Uri", "X-Original-URI", "X-Original-Uri"];

    public static IEndpointRouteBuilder MapSegmentAuthorization(this IEndpointRouteBuilder rotas)
    {
        rotas.MapMethods("/_authz", ["GET", "HEAD"], async (
            IOptions<StorageOptions> options,
            PlaybackService playback,
            CurrentViewer espectadores,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            if (!options.Value.SegmentAuthorization)
                return Results.StatusCode(StatusCodes.Status404NotFound);

            var caminho = EnderecoOriginal(contexto);

            if (ExtrairSegmento(caminho, options.Value.SegmentPath) is not { } segmento)
                return Results.Forbid();

            var espectador = await espectadores.GetAsync(cancellationToken);

            // Não conta visualização: o bilhete da playlist principal é que autoriza o resto.
            // A chave tem de ser a geração publicada, senão a anterior continua saindo.
            return await playback.CanReceiveMediaAsync(segmento.VideoId, espectador, segmento.Key, cancellationToken)
                ? Results.Ok()
                : Results.Forbid();
        });

        return rotas;
    }

    /// <summary>Endereço que o servidor da frente está tentando entregar.</summary>
    private static string EnderecoOriginal(HttpContext contexto)
    {
        foreach (var cabecalho in CabecalhosDeOrigem)
        {
            var valor = contexto.Request.Headers[cabecalho].ToString();

            if (!string.IsNullOrWhiteSpace(valor))
                return valor;
        }

        return contexto.Request.Query["uri"].ToString();
    }

    /// <summary>
    /// Extrai o vídeo do caminho do segmento. Devolve <c>null</c> para qualquer coisa fora do
    /// formato esperado, o que faz a autorização recusar em vez de adivinhar.
    /// </summary>
    public static Guid? ExtrairVideo(string? caminho, string prefixo) =>
        ExtrairSegmento(caminho, prefixo)?.VideoId;

    /// <summary>Vídeo e chave no bucket, já sem o prefixo público do caminho.</summary>
    public readonly record struct SegmentoPedido(Guid VideoId, string Key);

    /// <summary>
    /// Extrai o vídeo e a chave do caminho. Devolve <c>null</c> para qualquer coisa fora do
    /// formato esperado, o que faz a autorização recusar em vez de adivinhar.
    /// </summary>
    public static SegmentoPedido? ExtrairSegmento(string? caminho, string prefixo)
    {
        if (string.IsNullOrWhiteSpace(caminho))
            return null;

        var limpo = caminho.Split('?')[0].Trim();
        var raiz = "/" + prefixo.Trim('/') + "/";

        if (!limpo.StartsWith(raiz, StringComparison.Ordinal))
            return null;

        // O servidor da frente pode repassar o caminho sem normalizar. Qualquer coisa capaz de
        // mudar de pasta depois da conferência — "..", codificação, barra dupla ou invertida —
        // é recusada, em vez de depender de o storage rejeitar o caminho.
        if (limpo.Contains('%') || limpo.Contains('\\') || limpo.Contains("//", StringComparison.Ordinal))
            return null;

        var partes = limpo[raiz.Length..].Split('/');

        if (partes.Any(p => p is "" or "." or ".."))
            return null;

        return partes.Length >= 2 && Guid.TryParse(partes[0], out var videoId)
            ? new SegmentoPedido(videoId, string.Join('/', partes))
            : null;
    }
}
