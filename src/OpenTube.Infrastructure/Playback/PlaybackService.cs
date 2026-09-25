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
public readonly record struct PlaybackResult(bool Allowed, AccessReason Reason, string? Content)
{
    public static PlaybackResult Deny(AccessReason reason) => new(false, reason, null);

    public static PlaybackResult Allow(AccessReason reason, string content) => new(true, reason, content);
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
        var (video, resultado) = await AutorizarAsync(videoId, viewer, cancellationToken);

        if (video is null || !resultado.Allowed)
            return PlaybackResult.Deny(resultado.Reason);

        // A verificação fica na playlist principal, pedida uma vez por reprodução: fazê-la a
        // cada segmento transformaria o limite em um enxame de consultas.
        if (!await limite.AllowsAnotherAsync(viewer.UserId, ipHash, cancellationToken))
            return PlaybackResult.Deny(AccessReason.TooManyStreams);

        // O uso é registrado aqui, e não a cada segmento: a playlist principal é pedida uma
        // vez por reprodução, então é o ponto que corresponde a "assistiu".
        if (resultado.GrantId is { } concessao)
            await acesso.RegisterUseAsync(concessao, cancellationToken);

        var master = await storage.GetTextAsync(StorageBucket.Vod, StorageKeys.Master(videoId), cancellationToken);

        var reescrito = HlsManifestRewriter.Rewrite(master, uri =>
        {
            var versao = HlsManifestRewriter.RenditionFromVariantUri(uri);

            return versao is null ? uri : renditionUrl(versao);
        });

        return PlaybackResult.Allow(resultado.Reason, reescrito);
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

        if (video is null || !resultado.Allowed)
            return PlaybackResult.Deny(resultado.Reason);

        var chave = StorageKeys.RenditionPlaylist(videoId, rendition);
        var prefixo = StorageKeys.RenditionPrefix(videoId, rendition);

        var playlist = await storage.GetTextAsync(StorageBucket.Vod, chave, cancellationToken);

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

    private async Task<(Video? Video, AccessOutcome Outcome)> AutorizarAsync(Guid videoId, Viewer viewer, CancellationToken cancellationToken)
    {
        var video = await db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == videoId, cancellationToken);

        return video is null
            ? (null, new AccessOutcome(AccessDecision.Deny(AccessReason.PrivateVideo), null))
            : (video, await acesso.EvaluateAsync(viewer, video, cancellationToken));
    }
}
