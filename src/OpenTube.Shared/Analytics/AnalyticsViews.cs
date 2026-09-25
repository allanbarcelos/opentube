namespace OpenTube.Shared.Analytics;

/// <summary>Resumo de audiência de um vídeo.</summary>
/// <param name="Views">Sessões de reprodução.</param>
/// <param name="UniqueViewers">Pessoas distintas.</param>
/// <param name="WatchSeconds">Segundos únicos assistidos, somados.</param>
/// <param name="Completions">Sessões que chegaram ao fim.</param>
/// <param name="Errors">Sessões com erro de reprodução.</param>
/// <param name="DurationSeconds">Duração do vídeo.</param>
public sealed record VideoAudience(
    int Views, int UniqueViewers, double WatchSeconds, int Completions, int Errors, double DurationSeconds)
{
    /// <summary>Tempo médio assistido por sessão, em segundos.</summary>
    public double AverageWatchSeconds => Views <= 0 ? 0 : WatchSeconds / Views;

    /// <summary>Fração média do vídeo assistida por sessão.</summary>
    public double AverageCoverage =>
        DurationSeconds <= 0 || Views <= 0 ? 0 : Math.Clamp(AverageWatchSeconds / DurationSeconds, 0, 1);

    /// <summary>Fração das sessões que chegaram ao fim.</summary>
    public double CompletionRate => Views <= 0 ? 0 : Completions / (double)Views;

    public static VideoAudience Empty(double durationSeconds) => new(0, 0, 0, 0, 0, durationSeconds);
}

/// <summary>Quem assistiu a um vídeo e o quanto viu.</summary>
/// <param name="UserId">Pessoa autenticada, quando houver.</param>
/// <param name="Email">Endereço da pessoa, ou nulo para visitante anônimo.</param>
/// <param name="Sessions">Sessões desta pessoa no vídeo.</param>
/// <param name="WatchSeconds">Segundos únicos assistidos.</param>
/// <param name="Completed">Se chegou ao fim em alguma sessão.</param>
/// <param name="FirstAt">Primeira vez que assistiu.</param>
/// <param name="LastAt">Última vez que assistiu.</param>
/// <param name="Device">Aparelho mais recente.</param>
public sealed record VideoViewer(
    Guid? UserId, string? Email, int Sessions, double WatchSeconds, bool Completed,
    DateTimeOffset FirstAt, DateTimeOffset LastAt, string Device)
{
    public string DisplayName => Email ?? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
    {
        "pt" => "Visitante não identificado",
        "fr" => "Visiteur non identifié",
        _ => "Unidentified visitor"
    };
}

/// <summary>Um item da linha do tempo de uma pessoa.</summary>
/// <param name="VideoId">Vídeo assistido.</param>
/// <param name="Title">Título do vídeo.</param>
/// <param name="Slug">Endereço do vídeo.</param>
/// <param name="At">Quando começou.</param>
/// <param name="WatchSeconds">Segundos assistidos na sessão.</param>
/// <param name="DurationSeconds">Duração do vídeo.</param>
/// <param name="Completed">Se chegou ao fim.</param>
/// <param name="Device">Aparelho usado.</param>
public sealed record ViewerActivity(
    Guid VideoId, string Title, string Slug, DateTimeOffset At,
    double WatchSeconds, double DurationSeconds, bool Completed, string Device)
{
    public double Coverage => DurationSeconds <= 0 ? 0 : Math.Clamp(WatchSeconds / DurationSeconds, 0, 1);
}

/// <summary>Uma pessoa na listagem administrativa, com o resumo do que assistiu.</summary>
/// <param name="UserId">Identificador.</param>
/// <param name="Email">Endereço.</param>
/// <param name="IsAdmin">Se é administrador.</param>
/// <param name="IsActive">Se o acesso está ativo.</param>
/// <param name="CreatedAt">Quando passou a existir no sistema.</param>
/// <param name="LastSeenAt">Último acesso.</param>
/// <param name="VideosWatched">Vídeos distintos assistidos.</param>
/// <param name="WatchSeconds">Segundos assistidos somados.</param>
/// <param name="ActiveGrants">Concessões em vigor.</param>
public sealed record Person(
    Guid UserId, string Email, bool IsAdmin, bool IsActive,
    DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt,
    int VideosWatched, double WatchSeconds, int ActiveGrants);

/// <summary>Contagem de um valor qualquer, usada nos quadros de distribuição.</summary>
/// <param name="Label">Rótulo.</param>
/// <param name="Count">Quantidade.</param>
public sealed record AudienceBreakdown(string Label, int Count);

/// <summary>Números de um dia, para o gráfico de série.</summary>
/// <param name="Day">Dia.</param>
/// <param name="Views">Visualizações.</param>
/// <param name="WatchSeconds">Segundos assistidos.</param>
public sealed record DailyPoint(DateOnly Day, int Views, double WatchSeconds);

/// <summary>
/// O caminho entre convidar e assistir. É a métrica que costuma interessar de verdade numa
/// distribuição privada: quem ainda não abriu.
/// </summary>
/// <param name="Invited">Pessoas com concessão.</param>
/// <param name="SignedIn">Quantas já entraram alguma vez.</param>
/// <param name="Watched">Quantas começaram a assistir.</param>
/// <param name="Completed">Quantas chegaram ao fim.</param>
/// <param name="Pending">Endereços que ainda não assistiram.</param>
public sealed record InviteFunnel(
    int Invited, int SignedIn, int Watched, int Completed, IReadOnlyList<string> Pending);
