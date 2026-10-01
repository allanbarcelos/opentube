// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.Extensions.Options;
using OpenTube.Domain.Access;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Playback;
using OpenTube.Infrastructure.Services;
using OpenTube.Web.Auth;
using OpenTube.Web.Seguranca;

namespace OpenTube.Web.Endpoints;

/// <summary>
/// Autorização de cada arquivo de vídeo e da capa da coleção, para quando o servidor da
/// frente entrega os arquivos. A aplicação decide e o servidor transporta: assim a revogação
/// vale no segmento seguinte, e a aplicação não gasta banda nem memória com bytes de vídeo.
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
            PlaybackSeals selos,
            SegmentRateLimiter limite,
            CollectionThumbnailService miniaturas,
            CurrentViewer espectadores,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            if (!options.Value.SegmentAuthorization)
                return Results.StatusCode(StatusCodes.Status404NotFound);

            // O servidor da frente repassa os cabeçalhos do pedido original: abrir um arquivo de
            // vídeo direto numa aba é recusado aqui, antes de qualquer outra conferência.
            if (PedidoDoPlayer.EhAberturaDireta(contexto.Request))
                return Recusado();

            var caminho = EnderecoOriginal(contexto);
            var prefixo = options.Value.SegmentPath;
            var espectador = await espectadores.GetAsync(cancellationToken);

            // Os pedaços do vídeo chegam pelo endereço opaco. O selo diz o vídeo e o arquivo; a
            // resposta diz ao servidor da frente qual chave buscar no storage — ele não tem outra.
            if (SeloDoSegmento(caminho) is { } selo)
            {
                if (selos.Open(selo, PlaybackSealKind.Segment, espectador) is not { } aberto)
                    return Recusado();

                if (!limite.TryAcquire(PlaybackTokens.ViewerKey(espectador), aberto.VideoId))
                    return Results.StatusCode(StatusCodes.Status429TooManyRequests);

                // Não conta visualização: o bilhete da playlist principal é que autoriza o resto.
                if (await playback.AuthorizeSegmentAsync(aberto.VideoId, espectador, aberto.Path, cancellationToken) is not { } chave)
                    return Recusado();

                contexto.Response.Headers[StorageKeyHeader] = chave;
                return Results.Ok();
            }

            // A capa não fica numa pasta de vídeo. Sem este ramo o servidor da frente recusa
            // o endereço assinado e a imagem não carrega para quem já pode ver a coleção.
            if (ExtrairMiniaturaDeColecao(caminho, prefixo) is { } capa)
            {
                return await miniaturas.PodeEntregarAsync(capa.Key, espectador, cancellationToken)
                    ? Results.Ok()
                    : Results.Forbid();
            }

            if (ExtrairSegmento(caminho, prefixo) is not { } segmento)
                return Results.Forbid();

            // Pelo caminho legível saem só legendas, miniaturas e a folha de prévias, pedidas pela
            // página. Os pedaços do vídeo, ali, têm nome previsível: só pelo endereço opaco.
            if (EhMidia(segmento.Key))
                return Recusado();

            // A chave tem de ser a geração publicada, senão a anterior continua saindo.
            return await playback.CanReceiveMediaAsync(segmento.VideoId, espectador, segmento.Key, cancellationToken)
                ? Results.Ok()
                : Results.Forbid();
        });

        return rotas;
    }

    /// <summary>
    /// Recusa sem passar pela autenticação: para quem não entrou, <c>Forbid</c> vira um
    /// redirecionamento para a tela de entrada, e o servidor da frente o devolveria no lugar
    /// do arquivo.
    /// </summary>
    private static IResult Recusado() => Results.StatusCode(StatusCodes.Status403Forbidden);

    /// <summary>Segmento de vídeo ou arquivo de inicialização de uma versão.</summary>
    public static bool EhMidia(string chave) =>
        chave.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase)
        || chave.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

    /// <summary>Cabeçalho da resposta com a chave que o servidor da frente busca no storage.</summary>
    public const string StorageKeyHeader = "X-Storage-Key";

    /// <summary>Selo de um endereço <c>/s/{selo}</c>; nulo para qualquer outro.</summary>
    public static string? SeloDoSegmento(string? caminho)
    {
        if (string.IsNullOrWhiteSpace(caminho)
            || !caminho.StartsWith(PlaybackEndpoints.SegmentRoute, StringComparison.Ordinal))
            return null;

        var selo = caminho[PlaybackEndpoints.SegmentRoute.Length..].Split('?')[0];

        return selo.Length > 0 && selo.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? selo : null;
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

    /// <summary>Capa da coleção e a chave exata no bucket.</summary>
    public readonly record struct MiniaturaDeColecao(Guid CollectionId, string Key);

    /// <summary>
    /// Extrai o vídeo e a chave do caminho. Devolve <c>null</c> para qualquer coisa fora do
    /// formato esperado, o que faz a autorização recusar em vez de adivinhar.
    /// </summary>
    public static SegmentoPedido? ExtrairSegmento(string? caminho, string prefixo)
    {
        var partes = PartesDoCaminho(caminho, prefixo);

        return partes is { Length: >= 2 } && Guid.TryParse(partes[0], out var videoId)
            ? new SegmentoPedido(videoId, string.Join('/', partes))
            : null;
    }

    /// <summary>
    /// Extrai a capa no formato gravado pelo storage: <c>collections/{id}/thumb-{versão}.jpg</c>.
    /// Outra pasta, outra versão ou um caminho que muda de diretório não passa.
    /// </summary>
    public static MiniaturaDeColecao? ExtrairMiniaturaDeColecao(string? caminho, string prefixo)
    {
        if (PartesDoCaminho(caminho, prefixo) is not [var pasta, var idTexto, var arquivo])
            return null;

        if (!string.Equals(pasta, "collections", StringComparison.Ordinal)
            || !Guid.TryParseExact(idTexto, "n", out var collectionId)
            || !ArquivoDeMiniatura(arquivo))
            return null;

        return new MiniaturaDeColecao(collectionId, string.Join('/', ["collections", idTexto, arquivo]));
    }

    /// <summary>
    /// Partes do caminho depois do prefixo público. Devolve <c>null</c> quando o caminho pode
    /// escapar da pasta: codificação, barra duplicada, barra invertida ou <c>..</c>.
    /// </summary>
    private static string[]? PartesDoCaminho(string? caminho, string prefixo)
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

        return partes.Any(p => p is "" or "." or "..") ? null : partes;
    }

    /// <summary>Nome canônico <c>thumb-{versão}.jpg</c>, com versão positiva e sem zero à esquerda.</summary>
    private static bool ArquivoDeMiniatura(string arquivo)
    {
        const string inicio = "thumb-";
        const string fim = ".jpg";

        if (!arquivo.StartsWith(inicio, StringComparison.Ordinal) || !arquivo.EndsWith(fim, StringComparison.Ordinal))
            return false;

        var numero = arquivo[inicio.Length..^fim.Length];

        return numero.Length is > 0 and <= 19
            && numero.All(char.IsAsciiDigit)
            && numero[0] != '0'
            && long.TryParse(numero, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);
    }
}
