// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Dapper;
using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Access;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Access;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Shared.Catalog;

namespace OpenTube.Infrastructure.Playback;

/// <summary>
/// Lista e busca vídeos já filtrados pelo que o espectador pode ver. O filtro é aplicado na
/// consulta, e não depois: trazer tudo e esconder na interface deixaria títulos privados
/// passarem pela contagem, pela paginação e por qualquer descuido de apresentação.
/// </summary>
public class VideoCatalog(OpenTubeDbContext db, AccessService acesso, TimeProvider clock)
{
    public const int DefaultPageSize = 24;

    public async Task<PagedResult<VideoSummary>> BrowseAsync(
        Viewer viewer,
        string? query = null,
        int page = 1,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var termo = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var conexao = db.Database.GetDbConnection();

        // O filtro de acesso é aplicado na consulta, e não depois: trazer tudo e esconder na
        // interface deixaria títulos restritos passarem pela contagem e pela paginação.
        var filtro = FiltroDe(viewer);

        var busca = termo is null
            ? string.Empty
            : """
              AND (
                    v.search_vector @@ plainto_tsquery('portuguese_unaccent', @Termo)
                 OR v.title % @Termo
              )
              """;

        // O id no fim desempata: vídeos com a mesma data (um lote enviado junto, por exemplo)
        // não têm ordem garantida, e LIMIT/OFFSET poderia repetir um e pular outro entre páginas.
        var ordem = termo is null
            ? "ORDER BY COALESCE(v.published_at, v.created_at) DESC, v.id DESC"
            : """
              ORDER BY ts_rank(v.search_vector, plainto_tsquery('portuguese_unaccent', @Termo)) DESC,
                       similarity(v.title, @Termo) DESC,
                       COALESCE(v.published_at, v.created_at) DESC,
                       v.id DESC
              """;

        var parametros = Parametros(viewer, termo, page, pageSize);

        var total = await conexao.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM videos v WHERE {filtro} {busca}", parametros, cancellationToken: cancellationToken));

        if (total == 0)
            return PagedResult<VideoSummary>.Empty(pageSize);

        // As colunas são apelidadas para casar com as propriedades: o mapeamento por
        // construtor não aceita arranjo, e o esquema usa minúsculas com sublinhado.
        var linhas = await conexao.QueryAsync<VideoRow>(new CommandDefinition($"""
            SELECT v.id                AS Id,
                   v.slug              AS Slug,
                   v.title             AS Title,
                   v.description       AS Description,
                   v.duration_seconds  AS DurationSeconds,
                   v.visibility        AS Visibility,
                   v.status            AS Status,
                   v.published_at      AS PublishedAt,
                   v.created_at        AS CreatedAt,
                   v.tags              AS Tags,
                   {ColecaoDoVideo}    AS CollectionSlug,
                   EXISTS (
                       SELECT 1 FROM video_favorites f
                        WHERE f.video_id = v.id AND f.user_id = @Usuario
                   )                   AS IsFavorite,
                   {NovidadeDoVideo}   AS IsNew
              FROM videos v
             WHERE {filtro} {busca}
             {ordem}
             LIMIT @Limite OFFSET @Salto
            """, parametros, cancellationToken: cancellationToken));

        var itens = linhas.Select(Converter).ToList();

        return new PagedResult<VideoSummary>(itens, total, page, pageSize);
    }

    /// <summary>
    /// Página inicial sem busca. Um vídeo que está numa coleção não aparece sozinho: no lugar
    /// dele entra a coleção, uma vez. A exceção é o vídeo que esta pessoa favoritou: ele
    /// também aparece sozinho, e o clique continua abrindo a coleção nele. A busca continua
    /// vídeo a vídeo, mesmo dentro de coleção. A ordem é por nome: coleções favoritas, vídeos
    /// favoritos, as outras coleções e, por último, os vídeos que não estão em coleção.
    /// Maiúsculas e acentos não contam, e cada número vale pelo valor, então 2 fica antes de 10.
    /// </summary>
    public async Task<PagedResult<HomeCard>> HomeAsync(
        Viewer viewer,
        int page = 1,
        int pageSize = DefaultPageSize,
        bool groupCollections = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var conexao = db.Database.GetDbConnection();
        var itens = ItensDaHome(FiltroDe(viewer), groupCollections);
        var parametros = Parametros(viewer, null, page, pageSize);

        var total = await conexao.ExecuteScalarAsync<int>(new CommandDefinition(
            $"{itens} SELECT COUNT(*)::int FROM itens", parametros, cancellationToken: cancellationToken));

        if (total == 0)
            return PagedResult<HomeCard>.Empty(pageSize);

        var linhas = await conexao.QueryAsync<HomeRow>(new CommandDefinition($"""
            {itens}
            SELECT Id, Slug, Title, Description, VideoCount, DurationSeconds, Visibility, Status,
                   PublishedAt, CreatedAt, Tags, Kind, Favorite, ThumbnailVersion, CollectionSlug, HasNew
              FROM itens
             ORDER BY CASE
                          WHEN Kind = 1 AND Favorite THEN 0
                          WHEN Kind = 0 AND Favorite THEN 1
                          WHEN Kind = 1 THEN 2
                          ELSE 3
                      END,
                      opentube_natural_sort_key(Title), Id
             LIMIT @Limite OFFSET @Salto
            """, parametros, cancellationToken: cancellationToken));

        return new PagedResult<HomeCard>(linhas.Select(ConverterHome).ToList(), total, page, pageSize);
    }

    /// <summary>
    /// Vídeos de uma coleção na ordem da playlist. Devolve nulo quando a coleção não existe,
    /// está excluída, ou não tem nenhum vídeo que este espectador possa ver: o nome também
    /// não pode vazar.
    /// </summary>
    public async Task<PlaylistListing?> PlaylistAsync(
        Viewer viewer, string? slug, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        if (string.IsNullOrWhiteSpace(slug))
            return null;

        var conexao = db.Database.GetDbConnection();
        var parametros = Parametros(viewer, null, 1, 1);
        var filtro = FiltroDe(viewer);

        var colecao = await conexao.QuerySingleOrDefaultAsync<CollectionHead>(new CommandDefinition("""
            SELECT c.id          AS Id,
                   c.slug        AS Slug,
                   c.name        AS Name,
                   c.description AS Description,
                   EXISTS (
                       SELECT 1 FROM collection_favorites f
                        WHERE f.collection_id = c.id AND f.user_id = @Usuario
                   )             AS IsFavorite
              FROM collections c
             WHERE c.slug = @Slug AND c.deleted_at IS NULL
            """, new
        {
            Slug = slug.Trim(),
            parametros.Pronto,
            parametros.Agora,
            parametros.Email,
            parametros.Dominio,
            parametros.ConcessaoDeLink,
            parametros.Usuario
        }, cancellationToken: cancellationToken));

        if (colecao is null)
            return null;

        var linhas = await conexao.QueryAsync<VideoRow>(new CommandDefinition($"""
            SELECT v.id                AS Id,
                   v.slug              AS Slug,
                   v.title             AS Title,
                   v.description       AS Description,
                   v.duration_seconds  AS DurationSeconds,
                   v.visibility        AS Visibility,
                   v.status            AS Status,
                   v.published_at      AS PublishedAt,
                   v.created_at        AS CreatedAt,
                   v.tags              AS Tags,
                   {NovidadeNestaColecao} AS IsNew
              FROM collection_videos cv
              JOIN videos v ON v.id = cv.video_id
             WHERE cv.collection_id = @Colecao
               AND {filtro}
             ORDER BY cv.position, v.id
            """, new
        {
            Colecao = colecao.Id,
            parametros.Pronto,
            parametros.Agora,
            parametros.Email,
            parametros.Dominio,
            parametros.ConcessaoDeLink,
            parametros.Usuario
        }, cancellationToken: cancellationToken));

        var videos = linhas.Select(Converter).ToList();

        return videos.Count == 0
            ? null
            : new PlaylistListing(colecao.Id, colecao.Slug, colecao.Name, colecao.Description, colecao.IsFavorite, videos);
    }

    /// <summary>
    /// A coleção aparece para este espectador: não está excluída e tem ao menos um vídeo que
    /// ele pode ver. É a mesma regra da página inicial.
    /// </summary>
    public async Task<bool> CollectionIsVisibleAsync(Viewer viewer, Guid collectionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        var conexao = db.Database.GetDbConnection();
        var parametros = Parametros(viewer, null, 1, 1);
        var filtro = FiltroDe(viewer);

        return await conexao.ExecuteScalarAsync<bool>(new CommandDefinition($"""
            SELECT EXISTS (
                SELECT 1
                  FROM collections c
                  JOIN collection_videos cv ON cv.collection_id = c.id
                  JOIN videos v ON v.id = cv.video_id
                 WHERE c.id = @Colecao
                   AND c.deleted_at IS NULL
                   AND {filtro}
            )
            """, new
        {
            Colecao = collectionId,
            parametros.Pronto,
            parametros.Agora,
            parametros.Email,
            parametros.Dominio,
            parametros.ConcessaoDeLink,
            parametros.Usuario
        }, cancellationToken: cancellationToken));
    }

    /// <summary>Busca um vídeo pelo endereço legível, respeitando o acesso do espectador.</summary>
    public async Task<Video?> FindBySlugAsync(Viewer viewer, string slug, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        if (string.IsNullOrWhiteSpace(slug))
            return null;

        var video = await db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.Slug == slug, cancellationToken);

        if (video is null)
            return null;

        var resultado = await acesso.EvaluateAsync(viewer, video, cancellationToken);

        return resultado.Allowed ? video : null;
    }

    /// <summary>
    /// Vídeos que o espectador pode ver. Com o agrupamento ligado, a coleção ocupa o lugar
    /// dos vídeos dela, exceto o que esta pessoa favoritou. Desligado, cada vídeo aparece
    /// sozinho. Coleção excluída não conta: o vídeo volta a aparecer sozinho.
    /// </summary>
    private static string ItensDaHome(string filtro, bool agrupar)
    {
        var videos = $"""
            SELECT v.id                 AS Id,
                   v.slug               AS Slug,
                   v.title              AS Title,
                   v.description        AS Description,
                   1                    AS VideoCount,
                   v.duration_seconds   AS DurationSeconds,
                   v.visibility         AS Visibility,
                   v.status             AS Status,
                   v.published_at       AS PublishedAt,
                   v.created_at         AS CreatedAt,
                   v.tags               AS Tags,
                   v.ordenacao          AS Ordenacao,
                   0                    AS Kind,
                   EXISTS (
                       SELECT 1 FROM video_favorites f
                        WHERE f.video_id = v.id AND f.user_id = @Usuario) AS Favorite,
                   0::bigint            AS ThumbnailVersion,
                   {ColecaoDoVideo}     AS CollectionSlug,
                   {NovidadeDoVideo}    AS HasNew
              FROM visiveis v
            """;

        var soSoltos = agrupar
            ? """
              WHERE NOT EXISTS (
                    SELECT 1
                      FROM collection_videos cv
                      JOIN collections c ON c.id = cv.collection_id AND c.deleted_at IS NULL
                     WHERE cv.video_id = v.id)
                 OR EXISTS (
                    SELECT 1 FROM video_favorites f
                     WHERE f.video_id = v.id AND f.user_id = @Usuario)
              """
            : string.Empty;

        var colecoes = agrupar
            ? $"""
              UNION ALL
              SELECT c.id,
                     c.slug,
                     c.name,
                     c.description,
                     COUNT(*)::int,
                     COALESCE(SUM(v.duration_seconds), 0),
                     0,
                     0,
                     MAX(v.ordenacao),
                     MAX(v.ordenacao),
                     ARRAY[]::text[],
                     MAX(v.ordenacao),
                     1,
                     EXISTS (
                         SELECT 1 FROM collection_favorites f
                          WHERE f.collection_id = c.id AND f.user_id = @Usuario),
                     c.thumbnail_version,
                     NULL::text,
                     {NovidadeDaColecao}
                FROM collections c
                JOIN collection_videos cv ON cv.collection_id = c.id
                JOIN visiveis v ON v.id = cv.video_id
               WHERE c.deleted_at IS NULL
               GROUP BY c.id, c.slug, c.name, c.description, c.thumbnail_version
              """
            : string.Empty;

        return $"""
            WITH visiveis AS (
                SELECT v.id,
                       v.slug,
                       v.title,
                       v.description,
                       v.duration_seconds,
                       v.visibility,
                       v.status,
                       v.published_at,
                       v.created_at,
                       v.tags,
                       COALESCE(v.published_at, v.created_at) AS ordenacao
                  FROM videos v
                 WHERE {filtro}
            ),
            itens AS (
                {videos}
                {soSoltos}
                {colecoes}
            )
            """;
    }

    private static string FiltroDe(Viewer viewer) => viewer.IsAdmin
        ? "v.deleted_at IS NULL"
        : $"v.deleted_at IS NULL AND v.status = @Pronto AND {GrantSql.VideoVisivel}";

    private CatalogParameters Parametros(Viewer viewer, string? termo, int page, int pageSize) => new(
        (int)VideoStatus.Ready,
        termo,
        pageSize,
        (page - 1) * pageSize,
        clock.GetUtcNow(),
        viewer.Email,
        viewer.EmailDomain,
        viewer.LinkGrantId,
        viewer.UserId);

    private static VideoSummary Converter(VideoRow linha) => new(
        linha.Id, linha.Slug, linha.Title, linha.Description, linha.DurationSeconds,
        linha.Visibility, linha.Status, Momento(linha.PublishedAt), Momento(linha.CreatedAt)!.Value,
        linha.Tags ?? [], linha.CollectionSlug, linha.IsFavorite, linha.IsNew);

    private static HomeCard ConverterHome(HomeRow linha) => new(
        (HomeCardKind)linha.Kind,
        linha.Id, linha.Slug, linha.Title, linha.Description, linha.VideoCount, linha.DurationSeconds,
        linha.Visibility, linha.Status, Momento(linha.PublishedAt), Momento(linha.CreatedAt) ?? DateTimeOffset.UnixEpoch,
        linha.Tags ?? [], linha.Favorite, linha.ThumbnailVersion, linha.CollectionSlug, linha.HasNew);

    /// <summary>
    /// Coleção não excluída em que o vídeo está. Se houver mais de uma, vale a primeira pelo
    /// mesmo critério de nome da home. O clique abre essa coleção já neste vídeo.
    /// </summary>
    private const string ColecaoDoVideo = """
        (
            SELECT c.slug
              FROM collection_videos cv
              JOIN collections c ON c.id = cv.collection_id AND c.deleted_at IS NULL
             WHERE cv.video_id = v.id
             ORDER BY opentube_natural_sort_key(c.name), c.id
             LIMIT 1
        )
        """;

    /// <summary>
    /// O vídeo chegou numa coleção depois que esta pessoa foi criada, e ela ainda não o abriu.
    /// </summary>
    private const string NovidadeDoVideo = """
        EXISTS (
            SELECT 1
              FROM collection_videos cvn
              JOIN collections cn ON cn.id = cvn.collection_id AND cn.deleted_at IS NULL
              JOIN users un ON un.id = @Usuario
             WHERE cvn.video_id = v.id
               AND cvn.added_at > un.created_at
               AND NOT EXISTS (
                   SELECT 1 FROM collection_video_seen sn
                    WHERE sn.user_id = @Usuario
                      AND sn.collection_id = cvn.collection_id
                      AND sn.video_id = cvn.video_id)
        )
        """;

    /// <summary>A coleção tem algum vídeo visível que chegou depois desta pessoa, ainda não aberto.</summary>
    private const string NovidadeDaColecao = """
        EXISTS (
            SELECT 1
              FROM collection_videos cvn
              JOIN visiveis vn ON vn.id = cvn.video_id
              JOIN users un ON un.id = @Usuario
             WHERE cvn.collection_id = c.id
               AND cvn.added_at > un.created_at
               AND NOT EXISTS (
                   SELECT 1 FROM collection_video_seen sn
                    WHERE sn.user_id = @Usuario
                      AND sn.collection_id = cvn.collection_id
                      AND sn.video_id = cvn.video_id)
        )
        """;

    /// <summary>Este item da playlist é novo para a pessoa e ela ainda não o abriu.</summary>
    private const string NovidadeNestaColecao = """
        (
            @Usuario IS NOT NULL
            AND cv.added_at > (SELECT u.created_at FROM users u WHERE u.id = @Usuario)
            AND NOT EXISTS (
                SELECT 1 FROM collection_video_seen sn
                 WHERE sn.user_id = @Usuario
                   AND sn.collection_id = cv.collection_id
                   AND sn.video_id = cv.video_id)
        )
        """;

    /// <summary>
    /// O leitor devolve <c>timestamptz</c> como <see cref="DateTime"/> em UTC; o domínio
    /// trabalha com <see cref="DateTimeOffset"/>.
    /// </summary>
    private static DateTimeOffset? Momento(DateTime? valor) =>
        valor is null ? null : new DateTimeOffset(DateTime.SpecifyKind(valor.Value, DateTimeKind.Utc));

    /// <summary>Linha crua da consulta, preenchida por propriedade.</summary>
    private sealed class VideoRow
    {
        public Guid Id { get; init; }
        public string Slug { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string? Description { get; init; }
        public double DurationSeconds { get; init; }
        public int Visibility { get; init; }
        public int Status { get; init; }
        public DateTime? PublishedAt { get; init; }
        public DateTime CreatedAt { get; init; }
        public string[]? Tags { get; init; }
        public string? CollectionSlug { get; init; }
        public bool IsFavorite { get; init; }
        public bool IsNew { get; init; }
    }

    private sealed class HomeRow
    {
        public Guid Id { get; init; }
        public string Slug { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string? Description { get; init; }
        public int VideoCount { get; init; }
        public double DurationSeconds { get; init; }
        public int Visibility { get; init; }
        public int Status { get; init; }
        public DateTime? PublishedAt { get; init; }
        public DateTime CreatedAt { get; init; }
        public string[]? Tags { get; init; }
        public int Kind { get; init; }
        public bool Favorite { get; init; }
        public long ThumbnailVersion { get; init; }
        public string? CollectionSlug { get; init; }
        public bool HasNew { get; init; }
    }

    private sealed class CollectionHead
    {
        public Guid Id { get; init; }
        public string Slug { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string? Description { get; init; }
        public bool IsFavorite { get; init; }
    }

    private sealed record CatalogParameters(
        int Pronto,
        string? Termo,
        int Limite,
        int Salto,
        DateTimeOffset Agora,
        string? Email,
        string? Dominio,
        Guid? ConcessaoDeLink,
        Guid? Usuario);
}
