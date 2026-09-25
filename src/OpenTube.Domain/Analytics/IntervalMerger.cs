namespace OpenTube.Domain.Analytics;

/// <summary>
/// Funde trechos assistidos em faixas sem sobreposição. É o que distingue "assistiu dez
/// minutos" de "assistiu o mesmo minuto dez vezes": sem a fusão, reassistir um trecho
/// inflaria o tempo total e a plataforma mentiria sobre o quanto foi visto.
/// </summary>
public static class IntervalMerger
{
    /// <summary>
    /// Trechos que se tocam por menos que isto são unidos. Cobre a lacuna de arredondamento
    /// entre duas batidas consecutivas do player.
    /// </summary>
    public const double ToleranceSeconds = 0.5;

    /// <summary>Funde os trechos, devolvendo-os ordenados e sem sobreposição.</summary>
    public static IReadOnlyList<WatchInterval> Merge(IEnumerable<WatchInterval> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);

        var ordenados = intervals
            .Where(i => !i.IsEmpty)
            .OrderBy(i => i.Start)
            .ThenBy(i => i.End)
            .ToList();

        if (ordenados.Count == 0)
            return [];

        var resultado = new List<WatchInterval>(ordenados.Count);
        var atual = ordenados[0];

        foreach (var proximo in ordenados.Skip(1))
        {
            if (proximo.Start <= atual.End + ToleranceSeconds)
            {
                atual = atual with { End = Math.Max(atual.End, proximo.End) };
                continue;
            }

            resultado.Add(atual);
            atual = proximo;
        }

        resultado.Add(atual);

        return resultado;
    }

    /// <summary>Tempo único assistido, em segundos, já descontando as reexibições.</summary>
    public static double UniqueSeconds(IEnumerable<WatchInterval> intervals) =>
        Merge(intervals).Sum(i => i.Duration);

    /// <summary>
    /// Fração do vídeo assistida, entre 0 e 1. Vídeo sem duração conhecida devolve zero em
    /// vez de uma divisão por zero.
    /// </summary>
    public static double Coverage(IEnumerable<WatchInterval> intervals, double videoDuration) =>
        videoDuration <= 0 ? 0 : Math.Clamp(UniqueSeconds(intervals) / videoDuration, 0, 1);
}
