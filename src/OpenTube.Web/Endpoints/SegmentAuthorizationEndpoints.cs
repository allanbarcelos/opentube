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

            if (ExtrairVideo(caminho, options.Value.SegmentPath) is not { } videoId)
                return Results.Forbid();

            var espectador = await espectadores.GetAsync(cancellationToken);

            // A miniatura segue exatamente a mesma regra do vídeo, então serve de verificação
            // barata: não lê a playlist nem registra visualização.
            var autorizado = await playback.GetThumbnailUrlAsync(videoId, espectador, cancellationToken) is not null
                || (await playback.GetMasterAsync(videoId, espectador, v => v, cancellationToken: cancellationToken)).Allowed;

            return autorizado ? Results.Ok() : Results.Forbid();
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
    public static Guid? ExtrairVideo(string? caminho, string prefixo)
    {
        if (string.IsNullOrWhiteSpace(caminho))
            return null;

        var limpo = caminho.Split('?')[0].Trim();
        var raiz = "/" + prefixo.Trim('/') + "/";

        if (!limpo.StartsWith(raiz, StringComparison.Ordinal))
            return null;

        var partes = limpo[raiz.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);

        return partes.Length >= 2 && Guid.TryParse(partes[0], out var videoId) ? videoId : null;
    }
}
