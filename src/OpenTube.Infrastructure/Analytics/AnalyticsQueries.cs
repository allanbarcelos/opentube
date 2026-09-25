using Dapper;
using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Shared.Analytics;

namespace OpenTube.Infrastructure.Analytics;

/// <summary>
/// Consultas dos painéis. Ficam em SQL porque são agregações sobre milhares de linhas, onde
/// a tradução automática de consulta costuma render planos ruins.
/// </summary>
public class AnalyticsQueries(OpenTubeDbContext db)
{
    /// <summary>Resumo de audiência de um vídeo.</summary>
    public async Task<VideoAudience> VideoAudienceAsync(Guid videoId, CancellationToken cancellationToken = default)
    {
        var duracao = await db.Videos
            .Where(v => v.Id == videoId)
            .Select(v => v.DurationSeconds)
            .FirstOrDefaultAsync(cancellationToken);

        var linha = await db.Database.GetDbConnection().QuerySingleOrDefaultAsync<AudienceRow>(new CommandDefinition("""
            SELECT COUNT(*)                                             AS "Views",
                   COUNT(DISTINCT COALESCE(s.user_id::text, s.anonymous_id)) AS "UniqueViewers",
                   COALESCE(SUM(s.watched_seconds), 0)                  AS "WatchSeconds",
                   COUNT(*) FILTER (WHERE s.completed)                  AS "Completions",
                   COUNT(*) FILTER (WHERE s.error_count > 0)            AS "Errors"
              FROM playback_sessions s
             WHERE s.video_id = @VideoId
            """, new { VideoId = videoId }, cancellationToken: cancellationToken));

        return linha is null
            ? VideoAudience.Empty(duracao)
            : new VideoAudience(linha.Views, linha.UniqueViewers, linha.WatchSeconds, linha.Completions, linha.Errors, duracao);
    }

    /// <summary>Curva de retenção já agregada, em percentual sobre o total de espectadores.</summary>
    public async Task<IReadOnlyList<double>> RetentionAsync(Guid videoId, CancellationToken cancellationToken = default)
    {
        var fatias = await db.VideoRetentionBuckets
            .AsNoTracking()
            .Where(b => b.VideoId == videoId)
            .OrderBy(b => b.BucketIndex)
            .Select(b => b.Viewers)
            .ToListAsync(cancellationToken);

        if (fatias.Count == 0)
            return [];

        // A primeira fatia é o total de quem começou; é sobre ela que a queda é lida.
        var total = fatias.Count > 0 ? fatias.Max() : 0;

        return total <= 0 ? fatias.Select(_ => 0d).ToList() : Domain.Analytics.RetentionCurve.AsPercentages(fatias, total);
    }

    /// <summary>Quem assistiu ao vídeo, do mais recente para o mais antigo.</summary>
    public async Task<IReadOnlyList<VideoViewer>> ViewersAsync(Guid videoId, int limit = 200, CancellationToken cancellationToken = default)
    {
        var linhas = await db.Database.GetDbConnection().QueryAsync<ViewerRow>(new CommandDefinition("""
            SELECT s.user_id                                   AS "UserId",
                   u.email                                     AS "Email",
                   COUNT(*)                                    AS "Sessions",
                   COALESCE(MAX(s.watched_seconds), 0)         AS "WatchSeconds",
                   BOOL_OR(s.completed)                        AS "Completed",
                   MIN(s.started_at)                           AS "FirstAt",
                   MAX(s.started_at)                           AS "LastAt",
                   (ARRAY_AGG(s.device ORDER BY s.started_at DESC))[1] AS "Device"
              FROM playback_sessions s
              LEFT JOIN users u ON u.id = s.user_id
             WHERE s.video_id = @VideoId
             GROUP BY s.user_id, u.email, COALESCE(s.user_id::text, s.anonymous_id)
             ORDER BY MAX(s.started_at) DESC
             LIMIT @Limite
            """, new { VideoId = videoId, Limite = limit }, cancellationToken: cancellationToken));

        return [.. linhas.Select(l => new VideoViewer(
            l.UserId, l.Email, l.Sessions, l.WatchSeconds, l.Completed,
            Momento(l.FirstAt), Momento(l.LastAt), Aparelho(l.Device)))];
    }

    /// <summary>Tudo o que uma pessoa assistiu, do mais recente para o mais antigo.</summary>
    public async Task<IReadOnlyList<ViewerActivity>> ViewerTimelineAsync(Guid userId, int limit = 200, CancellationToken cancellationToken = default)
    {
        var linhas = await db.Database.GetDbConnection().QueryAsync<ActivityRow>(new CommandDefinition("""
            SELECT v.id               AS "VideoId",
                   v.title            AS "Title",
                   v.slug             AS "Slug",
                   s.started_at       AS "At",
                   s.watched_seconds  AS "WatchSeconds",
                   v.duration_seconds AS "DurationSeconds",
                   s.completed        AS "Completed",
                   s.device           AS "Device"
              FROM playback_sessions s
              JOIN videos v ON v.id = s.video_id
             WHERE s.user_id = @UserId
             ORDER BY s.started_at DESC
             LIMIT @Limite
            """, new { UserId = userId, Limite = limit }, cancellationToken: cancellationToken));

        return [.. linhas.Select(l => new ViewerActivity(
            l.VideoId, l.Title, l.Slug, Momento(l.At), l.WatchSeconds, l.DurationSeconds, l.Completed, Aparelho(l.Device)))];
    }

    /// <summary>Distribuição por aparelho, sistema ou navegador.</summary>
    public async Task<IReadOnlyList<AudienceBreakdown>> BreakdownAsync(
        Guid videoId, string dimension, CancellationToken cancellationToken = default)
    {
        // A dimensão vem de uma lista fixa: nunca é interpolada a partir de entrada externa.
        var coluna = dimension switch
        {
            "sistema" => "operating_system",
            "navegador" => "browser",
            _ => "device"
        };

        var linhas = await db.Database.GetDbConnection().QueryAsync<BreakdownRow>(new CommandDefinition($"""
            SELECT {coluna}::text AS "Label", COUNT(*) AS "Count"
              FROM playback_sessions
             WHERE video_id = @VideoId
             GROUP BY {coluna}
             ORDER BY COUNT(*) DESC
            """, new { VideoId = videoId }, cancellationToken: cancellationToken));

        return [.. linhas.Select(l => new AudienceBreakdown(
            coluna == "device" ? Aparelho(int.TryParse(l.Label, out var d) ? d : 0) : l.Label ?? "Desconhecido",
            l.Count))];
    }

    /// <summary>Série diária de um vídeo, a partir dos agregados.</summary>
    public async Task<IReadOnlyList<DailyPoint>> DailySeriesAsync(
        Guid videoId, int days = 30, CancellationToken cancellationToken = default)
    {
        var desde = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-days);

        var pontos = await db.VideoDailyStats
            .AsNoTracking()
            .Where(s => s.VideoId == videoId && s.Day >= desde)
            .OrderBy(s => s.Day)
            .Select(s => new DailyPoint(s.Day, s.Views, s.WatchSeconds))
            .ToListAsync(cancellationToken);

        return pontos;
    }

    /// <summary>
    /// O caminho entre convidar e assistir para um alvo. Responde a pergunta que mais importa
    /// numa distribuição privada: quem ainda não abriu.
    /// </summary>
    public async Task<InviteFunnel> InviteFunnelAsync(
        GrantTargetType targetType, Guid? targetId, CancellationToken cancellationToken = default)
    {
        var convidados = await db.AccessGrants
            .AsNoTracking()
            .Where(g => g.SubjectType == GrantSubjectType.User
                        && g.TargetType == targetType
                        && g.TargetId == targetId
                        && g.RevokedAt == null)
            .Select(g => g.SubjectValue)
            .ToListAsync(cancellationToken);

        if (convidados.Count == 0)
            return new InviteFunnel(0, 0, 0, 0, []);

        var usuarios = await db.Users
            .AsNoTracking()
            .Where(u => convidados.Contains(u.Email))
            .Select(u => new { u.Id, u.Email })
            .ToListAsync(cancellationToken);

        var ids = usuarios.Select(u => u.Id).ToList();

        var sessoes = await db.PlaybackSessions
            .AsNoTracking()
            .Where(s => s.UserId != null && ids.Contains(s.UserId.Value))
            .Where(s => targetType == GrantTargetType.All
                        || (targetType == GrantTargetType.Video && s.VideoId == targetId)
                        || db.CollectionVideos.Any(cv => cv.CollectionId == targetId && cv.VideoId == s.VideoId))
            .Select(s => new { s.UserId, s.Completed })
            .ToListAsync(cancellationToken);

        var assistiram = sessoes.Select(s => s.UserId!.Value).Distinct().ToHashSet();
        var concluiram = sessoes.Where(s => s.Completed).Select(s => s.UserId!.Value).Distinct().Count();

        var pendentes = usuarios
            .Where(u => !assistiram.Contains(u.Id))
            .Select(u => u.Email)
            .Concat(convidados.Where(e => usuarios.All(u => u.Email != e)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();

        return new InviteFunnel(convidados.Count, usuarios.Count, assistiram.Count, concluiram, pendentes);
    }

    /// <summary>
    /// Pessoas conhecidas pelo sistema, com o resumo do que assistiram. O filtro busca por
    /// parte do endereço, que é como o administrador costuma procurar alguém.
    /// </summary>
    public async Task<IReadOnlyList<Person>> PeopleAsync(
        string? search = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        var termo = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim().ToLowerInvariant() + "%";

        var linhas = await db.Database.GetDbConnection().QueryAsync<PersonRow>(new CommandDefinition("""
            SELECT u.id            AS "UserId",
                   u.email         AS "Email",
                   u.is_admin      AS "IsAdmin",
                   u.disabled_at IS NULL AS "IsActive",
                   u.created_at    AS "CreatedAt",
                   u.last_seen_at  AS "LastSeenAt",
                   COALESCE(a.videos, 0)   AS "VideosWatched",
                   COALESCE(a.segundos, 0) AS "WatchSeconds",
                   COALESCE(g.total, 0)    AS "ActiveGrants"
              FROM users u
              LEFT JOIN (
                    SELECT user_id,
                           COUNT(DISTINCT video_id) AS videos,
                           SUM(watched_seconds)     AS segundos
                      FROM playback_sessions
                     WHERE user_id IS NOT NULL
                     GROUP BY user_id
              ) a ON a.user_id = u.id
              LEFT JOIN (
                    SELECT subject_value, COUNT(*) AS total
                      FROM access_grants
                     WHERE subject_type = 1 AND revoked_at IS NULL
                     GROUP BY subject_value
              ) g ON g.subject_value = u.email
             WHERE @Termo IS NULL OR u.email LIKE @Termo
             ORDER BY u.last_seen_at DESC NULLS LAST, u.created_at DESC
             LIMIT @Limite
            """, new { Termo = termo, Limite = limit }, cancellationToken: cancellationToken));

        return [.. linhas.Select(l => new Person(
            l.UserId, l.Email, l.IsAdmin, l.IsActive,
            Momento(l.CreatedAt), l.LastSeenAt is null ? null : Momento(l.LastSeenAt.Value),
            l.VideosWatched, l.WatchSeconds, l.ActiveGrants))];
    }

    private static DateTimeOffset Momento(DateTime valor) =>
        new(DateTime.SpecifyKind(valor, DateTimeKind.Utc));

    private static string Aparelho(int valor) => ((DeviceType)valor) switch
    {
        DeviceType.Desktop => "Computador",
        DeviceType.Mobile => "Celular",
        DeviceType.Tablet => "Tablet",
        DeviceType.Tv => "Televisão",
        _ => "Desconhecido"
    };

    private sealed class AudienceRow
    {
        public int Views { get; init; }
        public int UniqueViewers { get; init; }
        public double WatchSeconds { get; init; }
        public int Completions { get; init; }
        public int Errors { get; init; }
    }

    private sealed class ViewerRow
    {
        public Guid? UserId { get; init; }
        public string? Email { get; init; }
        public int Sessions { get; init; }
        public double WatchSeconds { get; init; }
        public bool Completed { get; init; }
        public DateTime FirstAt { get; init; }
        public DateTime LastAt { get; init; }
        public int Device { get; init; }
    }

    private sealed class ActivityRow
    {
        public Guid VideoId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
        public DateTime At { get; init; }
        public double WatchSeconds { get; init; }
        public double DurationSeconds { get; init; }
        public bool Completed { get; init; }
        public int Device { get; init; }
    }

    private sealed class PersonRow
    {
        public Guid UserId { get; init; }
        public string Email { get; init; } = string.Empty;
        public bool IsAdmin { get; init; }
        public bool IsActive { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? LastSeenAt { get; init; }
        public int VideosWatched { get; init; }
        public double WatchSeconds { get; init; }
        public int ActiveGrants { get; init; }
    }

    private sealed class BreakdownRow
    {
        public string? Label { get; init; }
        public int Count { get; init; }
    }
}
