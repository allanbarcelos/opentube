// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Analytics;

/// <summary>
/// Curva de retenção: para cada fatia do vídeo, quantas pessoas ainda estavam assistindo.
/// É o que mostra onde o público abandona, coisa que a porcentagem média esconde.
/// </summary>
public static class RetentionCurve
{
    /// <summary>Quantidade padrão de fatias, uma por ponto percentual.</summary>
    public const int DefaultBuckets = 100;

    /// <summary>
    /// Conta quantos espectadores assistiram a cada fatia. Cada espectador entra com os
    /// próprios trechos já fundidos, para não contar duas vezes quem reviu o mesmo ponto.
    /// </summary>
    public static IReadOnlyList<int> Build(
        IEnumerable<IReadOnlyList<WatchInterval>> perViewer,
        double videoDuration,
        int buckets = DefaultBuckets)
    {
        ArgumentNullException.ThrowIfNull(perViewer);
        ArgumentOutOfRangeException.ThrowIfLessThan(buckets, 1);

        var contagem = new int[buckets];

        if (videoDuration <= 0)
            return contagem;

        var tamanhoDaFatia = videoDuration / buckets;

        foreach (var espectador in perViewer)
        {
            var trechos = IntervalMerger.Merge(espectador);

            for (var fatia = 0; fatia < buckets; fatia++)
            {
                // Basta o meio da fatia estar coberto: exigir a fatia inteira puniria quem
                // assistiu tudo mas teve um segundo perdido na troca de qualidade.
                var meio = (fatia + 0.5) * tamanhoDaFatia;

                if (trechos.Any(t => t.Contains(meio)))
                    contagem[fatia]++;
            }
        }

        return contagem;
    }

    /// <summary>
    /// Converte a contagem em percentual sobre o total de espectadores, que é como a curva
    /// costuma ser lida.
    /// </summary>
    public static IReadOnlyList<double> AsPercentages(IReadOnlyList<int> counts, int totalViewers)
    {
        ArgumentNullException.ThrowIfNull(counts);

        return totalViewers <= 0
            ? counts.Select(_ => 0d).ToList()
            : counts.Select(c => Math.Clamp(c * 100d / totalViewers, 0, 100)).ToList();
    }
}
