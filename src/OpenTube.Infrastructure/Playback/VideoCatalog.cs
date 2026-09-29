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
        var filtro = viewer.IsAdmin
            ? "v.deleted_at IS NULL"
            : $"v.deleted_at IS NULL AND v.status = @Pronto AND {GrantSql.VideoVisivel}";

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

        var parametros = new
        {
            Pronto = (int)VideoStatus.Ready,
            Termo = termo,
            Limite = pageSize,
            Salto = (page - 1) * pageSize,
            Agora = clock.GetUtcNow(),
            Email = viewer.Email,
            Dominio = viewer.EmailDomain,
            ConcessaoDeLink = viewer.LinkGrantId
        };

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
                   v.tags              AS Tags
              FROM videos v
             WHERE {filtro} {busca}
             {ordem}
             LIMIT @Limite OFFSET @Salto
            """, parametros, cancellationToken: cancellationToken));

        var itens = linhas.Select(Converter).ToList();

        return new PagedResult<VideoSummary>(itens, total, page, pageSize);
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

    private static VideoSummary Converter(VideoRow linha) => new(
        linha.Id, linha.Slug, linha.Title, linha.Description, linha.DurationSeconds,
        linha.Visibility, linha.Status, Momento(linha.PublishedAt), Momento(linha.CreatedAt)!.Value,
        linha.Tags ?? []);

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
    }
}
