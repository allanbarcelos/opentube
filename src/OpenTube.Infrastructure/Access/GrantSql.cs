// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Infrastructure.Access;

/// <summary>
/// Tradução da regra de concessão para SQL, usada quando a filtragem precisa acontecer no
/// banco — listagem, busca e contagem. Fica isolada aqui, e um teste de integração confere
/// que ela concorda com a política de acesso do domínio caso a caso.
/// </summary>
public static class GrantSql
{
    /// <summary>
    /// Condição de concessão em vigor. Espera os parâmetros <c>@Agora</c>, <c>@Email</c>,
    /// <c>@Dominio</c> e <c>@ConcessaoDeLink</c>, e o apelido <c>g</c> para a tabela.
    /// </summary>
    public const string ConcessaoEmVigor = """
        g.revoked_at IS NULL
        AND (g.starts_at IS NULL OR g.starts_at <= @Agora)
        AND (g.max_views IS NULL OR g.views_used < g.max_views)
        AND (
            CASE
                WHEN g.duration_after_first_use IS NULL OR g.first_used_at IS NULL
                    THEN g.expires_at
                ELSE LEAST(
                    g.first_used_at + g.duration_after_first_use,
                    COALESCE(g.expires_at, 'infinity'::timestamptz))
            END IS NULL
            OR CASE
                WHEN g.duration_after_first_use IS NULL OR g.first_used_at IS NULL
                    THEN g.expires_at
                ELSE LEAST(
                    g.first_used_at + g.duration_after_first_use,
                    COALESCE(g.expires_at, 'infinity'::timestamptz))
            END > @Agora
        )
        """;

    /// <summary>Condição de que a concessão se aplica a quem está pedindo.</summary>
    public const string SujeitoCorresponde = """
        (
            g.subject_type = 0
            OR (g.subject_type = 1 AND @Email IS NOT NULL AND g.subject_value = @Email)
            OR (g.subject_type = 2 AND @Dominio IS NOT NULL AND g.subject_value = @Dominio)
            OR (g.subject_type = 3 AND @ConcessaoDeLink IS NOT NULL AND g.id = @ConcessaoDeLink)
        )
        """;

    /// <summary>
    /// Condição de que a concessão alcança o vídeo <c>v</c>, inclusive por coleção.
    /// </summary>
    public const string AlvoAlcancaOVideo = """
        (
            g.target_type = 2
            OR (g.target_type = 0 AND g.target_id = v.id)
            OR (g.target_type = 1 AND g.target_id IN (
                    SELECT cv.collection_id FROM collection_videos cv WHERE cv.video_id = v.id))
        )
        """;

    /// <summary>
    /// Condição completa de visibilidade de um vídeo para quem não é administrador: público,
    /// ou restrito com uma concessão em vigor.
    /// </summary>
    public static string VideoVisivel { get; } = $"""
        (
            v.visibility = 1
            OR (
                v.visibility = 2
                AND EXISTS (
                    SELECT 1 FROM access_grants g
                     WHERE {ConcessaoEmVigor}
                       AND {SujeitoCorresponde}
                       AND {AlvoAlcancaOVideo}
                )
            )
        )
        """;
}
