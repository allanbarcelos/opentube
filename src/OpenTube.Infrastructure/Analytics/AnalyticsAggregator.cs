// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenTube.Domain.Analytics;
using OpenTube.Domain.Entities;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Analytics;

/// <summary>Quantos vídeos e dias foram recalculados numa passagem.</summary>
/// <param name="Videos">Vídeos com movimento no período.</param>
/// <param name="Days">Dias recalculados.</param>
public readonly record struct RollupResult(int Videos, int Days);

/// <summary>
/// Consolida as sessões em números por dia e na curva de retenção. O painel lê o resultado
/// pronto: recalcular a cada abertura significaria varrer todos os trechos de todas as
/// sessões, e a página ficaria mais lenta a cada mês de uso.
/// </summary>
public class AnalyticsAggregator(OpenTubeDbContext db, TimeProvider clock, ILogger<AnalyticsAggregator> logger)
{
    /// <summary>Recalcula os últimos dias, cobrindo sessões que continuaram depois da virada.</summary>
    public async Task<RollupResult> RollupRecentAsync(int days = 2, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);

        var hoje = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var videos = new HashSet<Guid>();

        for (var recuo = 0; recuo < days; recuo++)
        {
            var dia = hoje.AddDays(-recuo);

            foreach (var videoId in await RollupDayAsync(dia, cancellationToken))
                videos.Add(videoId);
        }

        foreach (var videoId in videos)
            await RebuildRetentionAsync(videoId, cancellationToken);

        logger.LogInformation("Agregação concluída: {Videos} vídeos em {Dias} dias", videos.Count, days);

        return new RollupResult(videos.Count, days);
    }

    /// <summary>Recalcula os números de um dia e devolve os vídeos que tiveram movimento.</summary>
    public async Task<IReadOnlyList<Guid>> RollupDayAsync(DateOnly day, CancellationToken cancellationToken = default)
    {
        var inicio = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var fim = inicio.AddDays(1);

        var sessoes = await db.PlaybackSessions
            .AsNoTracking()
            .Where(s => s.StartedAt >= inicio && s.StartedAt < fim)
            .Select(s => new
            {
                s.VideoId,
                s.UserId,
                s.AnonymousId,
                s.WatchedSeconds,
                s.Completed
            })
            .ToListAsync(cancellationToken);

        var porVideo = sessoes.GroupBy(s => s.VideoId).ToList();
        var comMovimento = porVideo.Select(g => g.Key).ToList();

        // Um dia pode ter perdido todas as sessões por exclusão de usuário; o registro
        // precisa ir a zero em vez de guardar o número antigo.
        var existentes = await db.VideoDailyStats
            .Where(s => s.Day == day)
            .ToListAsync(cancellationToken);

        foreach (var grupo in porVideo)
        {
            var visualizacoes = grupo.Count();
            var distintos = grupo
                .Select(s => s.UserId?.ToString() ?? s.AnonymousId ?? string.Empty)
                .Where(chave => chave.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Count();

            var segundos = grupo.Sum(s => s.WatchedSeconds);
            var conclusoes = grupo.Count(s => s.Completed);

            var registro = existentes.FirstOrDefault(s => s.VideoId == grupo.Key);

            if (registro is null)
                db.VideoDailyStats.Add(VideoDailyStat.Create(grupo.Key, day, visualizacoes, distintos, segundos, conclusoes));
            else
                registro.Update(visualizacoes, distintos, segundos, conclusoes);
        }

        foreach (var orfao in existentes.Where(s => !comMovimento.Contains(s.VideoId)))
            db.VideoDailyStats.Remove(orfao);

        await db.SaveChangesAsync(cancellationToken);

        return comMovimento;
    }

    /// <summary>
    /// Refaz a curva de retenção de um vídeo a partir de todo o histórico. Cada pessoa entra
    /// uma vez, com os trechos fundidos, para que reassistir não infle a curva.
    /// </summary>
    public async Task RebuildRetentionAsync(Guid videoId, CancellationToken cancellationToken = default)
    {
        var duracao = await db.Videos
            .Where(v => v.Id == videoId)
            .Select(v => v.DurationSeconds)
            .FirstOrDefaultAsync(cancellationToken);

        if (duracao <= 0)
            return;

        var trechos = await db.PlaybackIntervals
            .AsNoTracking()
            .Join(db.PlaybackSessions.AsNoTracking().Where(s => s.VideoId == videoId),
                i => i.SessionId,
                s => s.Id,
                (i, s) => new
                {
                    Espectador = s.UserId != null ? s.UserId.ToString()! : s.AnonymousId ?? string.Empty,
                    i.StartSeconds,
                    i.EndSeconds
                })
            .ToListAsync(cancellationToken);

        var porEspectador = trechos
            .GroupBy(t => t.Espectador, StringComparer.Ordinal)
            .Select(g => (IReadOnlyList<WatchInterval>)g
                .Select(t => new WatchInterval(t.StartSeconds, t.EndSeconds))
                .ToList())
            .ToList();

        var curva = RetentionCurve.Build(porEspectador, duracao);

        var existentes = await db.VideoRetentionBuckets
            .Where(b => b.VideoId == videoId)
            .ToListAsync(cancellationToken);

        for (var fatia = 0; fatia < curva.Count; fatia++)
        {
            var registro = existentes.FirstOrDefault(b => b.BucketIndex == fatia);

            if (registro is null)
                db.VideoRetentionBuckets.Add(VideoRetentionBucket.Create(videoId, fatia, curva[fatia]));
            else
                registro.Update(curva[fatia]);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Descarta eventos brutos antigos demais. Os agregados permanecem: o que se perde é a
    /// capacidade de investigar um caso pontual, não o histórico de audiência.
    /// </summary>
    public async Task<int> PruneEventsAsync(TimeSpan retention, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);

        var corte = clock.GetUtcNow() - retention;

        return await db.PlaybackEvents
            .Where(e => e.At < corte)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
