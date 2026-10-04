// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.Extensions.Options;
using OpenTube.Domain.Access;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Media;
using OpenTube.Infrastructure.Branding;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Services;
using OpenTube.Shared.Catalog;

namespace OpenTube.Infrastructure.Playback;

/// <summary>Um trecho da barra de progresso: um capítulo, ou o começo antes do primeiro.</summary>
/// <param name="Start">Início, em segundos.</param>
/// <param name="End">Fim, em segundos.</param>
/// <param name="Title">Título do capítulo; vazio no trecho antes do primeiro.</param>
public sealed record ProgressSegment(int Start, int End, string Title);

/// <summary>Tudo o que a página do vídeo exibe, montado de uma vez.</summary>
/// <param name="Video">O vídeo.</param>
/// <param name="Playlist">A coleção aberta junto, quando o vídeo está nela.</param>
/// <param name="IsFavorite">Se a pessoa favoritou o vídeo.</param>
/// <param name="Captions">Legendas prontas para o player.</param>
/// <param name="Chapters">Sumário do vídeo.</param>
/// <param name="Segments">Trechos da barra de progresso, um por capítulo.</param>
/// <param name="MyRating">A nota que a pessoa deu, se deu.</param>
/// <param name="Logo">Marca d'água do acervo, quando definida.</param>
/// <param name="PlaybackToken">Token que o player troca pelo endereço da reprodução.</param>
/// <param name="WatermarkText">Identificação de quem assiste, exibida sobre o vídeo.</param>
public sealed record WatchPage(
    Video Video,
    PlaylistListing? Playlist,
    bool IsFavorite,
    IReadOnlyList<VideoAsset> Captions,
    IReadOnlyList<Chapter> Chapters,
    IReadOnlyList<ProgressSegment> Segments,
    int? MyRating,
    WatermarkInfo? Logo,
    string PlaybackToken,
    string? WatermarkText);

/// <summary>
/// Monta a página do vídeo. A página só exibe; quem decide o que ela mostra e reúne as partes —
/// catálogo, playlist, favorito, legendas, sumário, nota, marca e token — é este serviço.
/// </summary>
/// <remarks>
/// As consultas vão uma depois da outra: todas usam o contexto do banco do pedido, que não
/// aceita duas operações ao mesmo tempo.
/// </remarks>
public class WatchPageService(
    VideoCatalog catalog,
    CollectionNoticeService notices,
    VideoFavoriteService favorites,
    CaptionService captions,
    VideoChapterService chapters,
    VideoRatingService ratings,
    WatermarkService watermarks,
    PlaybackTokens tokens,
    IOptions<SecurityOptions> options)
{
    /// <summary>
    /// Abre o vídeo para quem assiste. Nulo quando ele não existe ou não é para essa pessoa — os
    /// dois casos respondem igual, para não revelar o que há no acervo. Abrir é também o clique
    /// que apaga o aviso de novidade do vídeo.
    /// </summary>
    public async Task<WatchPage?> OpenAsync(
        Viewer viewer, string slug, string? collectionSlug, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        // Abrir a página é pedir uma reprodução nova: o bilhete de uma reprodução anterior não
        // pode mostrar o player a quem já esgotou as visualizações.
        var video = await catalog.FindBySlugAsync(viewer.StartingNewView(), slug, cancellationToken);

        if (video is null)
            return null;

        var favorito = false;
        if (viewer.UserId is Guid usuario)
        {
            await notices.MarkSeenAsync(usuario, video.Id, cancellationToken);
            favorito = await favorites.IsFavoriteAsync(usuario, video.Id, cancellationToken);
        }

        var playlist = await catalog.PlaylistAsync(viewer, collectionSlug, cancellationToken);
        var sumario = await chapters.ListAsync(video.Id, cancellationToken);

        return new WatchPage(
            video,
            playlist is not null && playlist.Contains(video.Id) ? playlist : null,
            favorito,
            await captions.ListPlayableAsync(video.Id, cancellationToken),
            sumario,
            Segments(sumario, TimeSpan.FromSeconds(video.DurationSeconds)),
            await ratings.GetMineAsync(video.Id, viewer, cancellationToken),
            await watermarks.GetInfoAsync(cancellationToken),
            tokens.Issue(video.Id, viewer),
            WatermarkText(viewer, options.Value.WatermarkEnabled));
    }

    /// <summary>
    /// Trechos da barra de progresso: um por capítulo e, se o primeiro não começa no zero, um
    /// trecho sem título antes dele. Sem capítulos ou sem duração, nenhum.
    /// </summary>
    public static IReadOnlyList<ProgressSegment> Segments(IReadOnlyList<Chapter> chapters, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(chapters);

        if (chapters.Count == 0 || duration <= TimeSpan.Zero)
            return [];

        var trechos = new List<ProgressSegment>(chapters.Count + 1);

        if (chapters[0].StartSeconds > 0)
            trechos.Add(new ProgressSegment(0, chapters[0].StartSeconds, string.Empty));

        for (var i = 0; i < chapters.Count; i++)
            trechos.Add(new ProgressSegment(chapters[i].StartSeconds, VideoChapters.EndOf(chapters, i, duration), chapters[i].Title));

        return trechos;
    }

    /// <summary>
    /// Identificação sobre o vídeo. Não impede a gravação de tela, mas identifica a origem de um
    /// vazamento e inibe o repasse casual. Quem entrou pelo link secreto não tem email: vai o
    /// começo do identificador da concessão, que a administração encontra na lista de acessos.
    /// Vídeo público visto por anônimo fica sem marca: não há de quem seria o vazamento.
    /// </summary>
    public static string? WatermarkText(Viewer viewer, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        if (!enabled)
            return null;

        return viewer.IsAuthenticated
            ? viewer.Email
            : viewer.LinkGrantId is { } link ? $"link {link.ToString("n")[..8]}" : null;
    }
}
