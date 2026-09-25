namespace OpenTube.Domain.Entities;

/// <summary>
/// Números de um vídeo em um dia, pré-agregados. O painel consulta esta tabela em vez de
/// varrer os eventos brutos, o que mantém a resposta imediata mesmo com milhões de linhas.
/// </summary>
public class VideoDailyStat
{
    private VideoDailyStat() { }

    public Guid VideoId { get; private set; }

    /// <summary>Dia em UTC.</summary>
    public DateOnly Day { get; private set; }

    /// <summary>Sessões de reprodução iniciadas.</summary>
    public int Views { get; private set; }

    /// <summary>Pessoas distintas, contando visitante anônimo pelo identificador do navegador.</summary>
    public int UniqueViewers { get; private set; }

    /// <summary>Soma dos segundos únicos assistidos.</summary>
    public double WatchSeconds { get; private set; }

    /// <summary>Quantas sessões chegaram ao fim do vídeo.</summary>
    public int Completions { get; private set; }

    public static VideoDailyStat Create(Guid videoId, DateOnly day, int views, int uniqueViewers, double watchSeconds, int completions) => new()
    {
        VideoId = videoId,
        Day = day,
        Views = views,
        UniqueViewers = uniqueViewers,
        WatchSeconds = watchSeconds,
        Completions = completions
    };

    public void Update(int views, int uniqueViewers, double watchSeconds, int completions)
    {
        Views = views;
        UniqueViewers = uniqueViewers;
        WatchSeconds = watchSeconds;
        Completions = completions;
    }

    /// <summary>Tempo médio assistido por sessão, em segundos.</summary>
    public double AverageWatchSeconds => Views <= 0 ? 0 : WatchSeconds / Views;
}
