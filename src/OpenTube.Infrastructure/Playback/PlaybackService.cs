// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Access;
using OpenTube.Domain.Entities;
using OpenTube.Infrastructure.Access;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Storage;

namespace OpenTube.Infrastructure.Playback;

/// <summary>Resultado de um pedido de reprodução.</summary>
/// <param name="Allowed">Se o espectador pode assistir.</param>
/// <param name="Reason">Motivo da decisão, preservado para auditoria.</param>
/// <param name="Content">Conteúdo da playlist, quando permitido.</param>
/// <param name="Ticket">
/// Bilhete da reprodução que acabou de ter a visualização contada, para que os pedidos
/// seguintes dela não esbarrem no teto de visualizações.
/// </param>
public readonly record struct PlaybackResult(bool Allowed, AccessReason Reason, string? Content, IssuedTicket? Ticket = null)
{
    public static PlaybackResult Deny(AccessReason reason) => new(false, reason, null);

    public static PlaybackResult Allow(AccessReason reason, string content, IssuedTicket? ticket = null) =>
        new(true, reason, content, ticket);
}

/// <summary>
/// Entrega as playlists de reprodução. A autorização é verificada nos dois níveis — na
/// playlist principal e na de cada versão — para que um endereço de versão copiado não
/// contorne a decisão tomada na entrada.
/// </summary>
public class PlaybackService(
    OpenTubeDbContext db,
    IVideoStorage storage,
    AccessService acesso,
    PlaybackGuard limite,
    PlaybackTickets bilhetes,
    IOptions<StorageOptions> options)
{
    private readonly StorageOptions _options = options.Value;

    /// <summary>Playlist principal, com as variantes apontando de volta para a aplicação.</summary>
    public async Task<PlaybackResult> GetMasterAsync(
        Guid videoId,
        Viewer viewer,
        Func<string, string> renditionUrl,
        string? ipHash = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        // A playlist principal abre uma reprodução nova: ela precisa caber no teto de
        // visualizações por conta própria, sem se apoiar no bilhete de uma reprodução anterior.
        var (video, resultado) = await AutorizarAsync(videoId, viewer.StartingNewView(), cancellationToken);

        if (video is null || !resultado.Allowed)
            return PlaybackResult.Deny(resultado.Reason);

        // A verificação fica na playlist principal, pedida uma vez por reprodução: fazê-la a
        // cada segmento transformaria o limite em um enxame de consultas.
        if (!await limite.AllowsAnotherAsync(viewer.UserId, ipHash, cancellationToken))
            return PlaybackResult.Deny(AccessReason.TooManyStreams);

        // Lê antes de contar. Se o storage falhar, a visualização não é consumida. Uma chave
        // ausente vira "não encontrado", nunca um erro: o vídeo está liberado, mas o arquivo
        // não está lá (uma geração trocada, um arquivo que sumiu).
        var master = await storage.TryGetTextAsync(StorageBucket.Vod, StorageKeys.MasterUnder(Prefixo(video)), cancellationToken);

        if (master is null)
            return PlaybackResult.Deny(AccessReason.MediaMissing);

        var reescrito = HlsManifestRewriter.Rewrite(master, uri =>
        {
            var versao = HlsManifestRewriter.RenditionFromVariantUri(uri);

            return versao is null ? uri : renditionUrl(versao);
        });

        // O uso é registrado aqui, e não a cada segmento: a playlist principal é pedida uma
        // vez por reprodução, então é o ponto que corresponde a "assistiu".
        IssuedTicket? bilhete = null;

        if (resultado.GrantId is { } concessao)
        {
            if (!await acesso.RegisterUseAsync(concessao, cancellationToken))
                return PlaybackResult.Deny(AccessReason.GrantExhausted);

            bilhete = bilhetes.Issue(videoId, concessao, video.DurationSeconds);
        }

        return PlaybackResult.Allow(resultado.Reason, reescrito, bilhete);
    }

    /// <summary>
    /// Playlist de uma versão, com segmentos e arquivo de inicialização já assinados. Os
    /// endereços assinados valem por algumas horas: tempo suficiente para assistir, curto o
    /// bastante para que um endereço copiado não circule indefinidamente.
    /// </summary>
    public async Task<PlaybackResult> GetRenditionAsync(
        Guid videoId,
        string rendition,
        Viewer viewer,
        CancellationToken cancellationToken = default)
    {
        var (video, resultado) = await AutorizarAsync(videoId, viewer, cancellationToken);

        if (video is null || !resultado.Allowed || !ContinuacaoOk(videoId, viewer, resultado))
            return PlaybackResult.Deny(video is null || !resultado.Allowed ? resultado.Reason : AccessReason.GrantExhausted);

        var chave = StorageKeys.RenditionPlaylistUnder(Prefixo(video), rendition);
        var prefixo = StorageKeys.RenditionPrefixUnder(Prefixo(video), rendition);

        // Uma versão que não existe (um nome qualquer na URL) não é erro do servidor: o arquivo
        // simplesmente não está no storage, e a resposta é "não encontrado".
        var playlist = await storage.TryGetTextAsync(StorageBucket.Vod, chave, cancellationToken);

        if (playlist is null)
            return PlaybackResult.Deny(AccessReason.MediaMissing);

        var reescrito = HlsManifestRewriter.Rewrite(playlist, uri =>
        {
            if (uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return uri;

            // Com autorização por pedido, o segmento sai por um caminho da própria aplicação
            // e a revogação passa a valer no segmento seguinte, em vez de esperar a
            // assinatura vencer.
            return _options.SegmentAuthorization
                ? $"{_options.SegmentPath.TrimEnd('/')}/{prefixo}{uri}"
                : storage.SignDownloadUrl(StorageBucket.Vod, prefixo + uri, _options.PlaybackUrlLifetime);
        });

        return PlaybackResult.Allow(resultado.Reason, reescrito);
    }

    /// <summary>Endereço assinado da miniatura, ou <c>null</c> quando não há acesso.</summary>
    public async Task<string?> GetThumbnailUrlAsync(Guid videoId, Viewer viewer, CancellationToken cancellationToken = default)
    {
        var (video, resultado) = await AutorizarAsync(videoId, viewer, cancellationToken);

        if (video?.ThumbnailKey is null || !resultado.Allowed)
            return null;

        return storage.SignDownloadUrl(StorageBucket.Vod, video.ThumbnailKey, _options.PlaybackUrlLifetime);
    }

    /// <summary>
    /// Só responde se o espectador pode assistir, sem registrar visualização e sem ler o
    /// storage. Não entrega mídia: segmentos e legendas passam por <see cref="CanReceiveMediaAsync"/>.
    /// </summary>
    public async Task<bool> CanWatchAsync(Guid videoId, Viewer viewer, CancellationToken cancellationToken = default)
    {
        var (video, resultado) = await AutorizarAsync(videoId, viewer, cancellationToken);

        return video is not null && resultado.Allowed;
    }

    /// <summary>
    /// Autoriza a entrega de um arquivo já publicado. Com teto de visualizações, exige o
    /// bilhete da reprodução contada — senão a playlist de versão, o segmento e a legenda
    /// contornam o limite pulando a playlist principal. A chave, quando informada, tem de
    /// estar na geração publicada ou nas legendas: uma geração substituída deixa de ser servida
    /// mesmo que o arquivo ainda exista.
    /// </summary>
    public async Task<bool> CanReceiveMediaAsync(Guid videoId, Viewer viewer, string? objectKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        var (video, resultado) = await AutorizarAsync(videoId, viewer, cancellationToken);

        if (video is null || !resultado.Allowed || !ContinuacaoOk(videoId, viewer, resultado))
            return false;

        return objectKey is null || ChavePublicada(video, objectKey);
    }

    /// <summary>
    /// Concessão com teto só entrega o resto da reprodução a quem já teve a visualização
    /// contada. Sem isso, pedir a versão direto não passa pelo incremento.
    /// </summary>
    private static bool ContinuacaoOk(Guid videoId, Viewer viewer, AccessOutcome resultado) =>
        !resultado.CountsViews || (resultado.GrantId is { } concessao && viewer.IsContinuing(videoId, concessao));

    private static bool ChavePublicada(Video video, string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey) || objectKey.Contains("..", StringComparison.Ordinal))
            return false;

        return objectKey.StartsWith(Prefixo(video), StringComparison.Ordinal)
            || objectKey.StartsWith(StorageKeys.CaptionsPrefix(video.Id), StringComparison.Ordinal);
    }

    /// <summary>
    /// Prefixo da versão publicada. Vídeos processados antes do versionamento guardam o
    /// prefixo raiz do vídeo, que continua funcionando.
    /// </summary>
    private static string Prefixo(Video video) =>
        string.IsNullOrWhiteSpace(video.HlsPrefix) ? StorageKeys.VodPrefix(video.Id) : video.HlsPrefix;

    private async Task<(Video? Video, AccessOutcome Outcome)> AutorizarAsync(Guid videoId, Viewer viewer, CancellationToken cancellationToken)
    {
        var video = await db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == videoId, cancellationToken);

        return video is null
            ? (null, new AccessOutcome(AccessDecision.Deny(AccessReason.PrivateVideo), null))
            : (video, await acesso.EvaluateAsync(viewer, video, cancellationToken));
    }
}
