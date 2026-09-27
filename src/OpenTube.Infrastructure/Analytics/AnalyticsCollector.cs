// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenTube.Domain.Analytics;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Shared.Analytics;

namespace OpenTube.Infrastructure.Analytics;

/// <summary>
/// Recebe o que o player relata e mantém a sessão de reprodução em dia. Guarda os trechos
/// assistidos já fundidos e o evento bruto, que sustenta investigações pontuais e pode ser
/// descartado por idade sem afetar os números agregados.
/// </summary>
public class AnalyticsCollector(OpenTubeDbContext db, TimeProvider clock, ILogger<AnalyticsCollector> logger)
{
    /// <summary>Depois disso sem sinal, a sessão é considerada encerrada.</summary>
    public static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(10);

    public async Task<PlaybackSession> StartAsync(
        Guid videoId,
        Guid? userId,
        string? anonymousId,
        Guid? grantId,
        string? userAgent,
        string? ipHash,
        string? referrer,
        CancellationToken cancellationToken = default)
    {
        var agora = clock.GetUtcNow();

        var sessao = PlaybackSession.Start(
            videoId, agora, userId, anonymousId, grantId,
            UserAgentParser.Parse(userAgent), ipHash, null, referrer);

        db.PlaybackSessions.Add(sessao);
        db.PlaybackEvents.Add(PlaybackEvent.Create(
            sessao.Id, videoId, userId, PlaybackEventType.Start, agora, 0));

        await db.SaveChangesAsync(cancellationToken);

        return sessao;
    }

    /// <summary>
    /// Aplica um lote de relatos. A sessão é conferida contra quem está pedindo: sem isso,
    /// bastaria adivinhar um identificador para poluir o histórico de outra pessoa.
    /// </summary>
    public async Task<bool> RecordAsync(
        Guid sessionId,
        Guid? userId,
        string? anonymousId,
        PlaybackBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var sessao = await db.PlaybackSessions
            .Include(s => s.Intervals)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (sessao is null || !Pertence(sessao, userId, anonymousId))
            return false;

        var duracao = await db.Videos
            .Where(v => v.Id == sessao.VideoId)
            .Select(v => v.DurationSeconds)
            .FirstOrDefaultAsync(cancellationToken);

        var agora = clock.GetUtcNow();

        foreach (var relato in batch.Eventos ?? [])
            Aplicar(sessao, relato, agora, duracao, userId);

        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>Encerra a sessão quando o player avisa que a página está sendo fechada.</summary>
    public async Task EndAsync(Guid sessionId, Guid? userId, string? anonymousId, CancellationToken cancellationToken = default)
    {
        var sessao = await db.PlaybackSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (sessao is null || !Pertence(sessao, userId, anonymousId))
            return;

        sessao.End(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Fecha sessões que pararam de dar sinal. O navegador nem sempre consegue avisar que a
    /// aba foi fechada, então a ausência de batidas é o sinal mais confiável.
    /// </summary>
    public async Task<int> CloseStaleAsync(CancellationToken cancellationToken = default)
    {
        var corte = clock.GetUtcNow() - InactivityTimeout;

        return await db.PlaybackSessions
            .Where(s => s.EndedAt == null && s.LastSeenAt < corte)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.EndedAt, x => x.LastSeenAt), cancellationToken);
    }

    /// <summary>
    /// Garante que exista partição para o mês informado e para o seguinte. É chamada pelo
    /// worker: sem partição, a gravação cairia na partição de escape e perderia a vantagem
    /// do descarte por idade.
    /// </summary>
    public async Task EnsurePartitionsAsync(DateTimeOffset reference, CancellationToken cancellationToken = default)
    {
        foreach (var mes in new[] { reference, reference.AddMonths(1) })
        {
            await db.Database.ExecuteSqlRawAsync(
                "SELECT opentube_ensure_event_partition({0}::date)",
                [new DateOnly(mes.Year, mes.Month, 1)],
                cancellationToken);
        }

        logger.LogDebug("Partições de eventos garantidas até {Mes:yyyy-MM}", reference.AddMonths(1));
    }

    private void Aplicar(PlaybackSession sessao, PlaybackEventReport relato, DateTimeOffset agora, double duracao, Guid? userId)
    {
        var tipo = Traduzir(relato.Tipo);

        switch (tipo)
        {
            case PlaybackEventType.Progress when relato is { De: { } de, Ate: { } ate }:
                sessao.Record(WatchInterval.Create(de, ate, duracao), agora, duracao);
                break;

            case PlaybackEventType.Seek:
                sessao.Seek(relato.Em, agora);
                break;

            case PlaybackEventType.Quality:
                sessao.RecordQuality(relato.Detalhe, agora);
                break;

            case PlaybackEventType.Error:
                sessao.RecordError(agora);
                break;

            case PlaybackEventType.Ended:
                sessao.MarkCompleted(agora);
                break;
        }

        db.PlaybackEvents.Add(PlaybackEvent.Create(
            sessao.Id, sessao.VideoId, userId, tipo, agora, relato.Em, relato.De, relato.Ate, relato.Detalhe));
    }

    /// <summary>A sessão só aceita relatos de quem a abriu.</summary>
    private static bool Pertence(PlaybackSession sessao, Guid? userId, string? anonymousId) =>
        sessao.UserId is { } dono
            ? dono == userId
            : !string.IsNullOrWhiteSpace(anonymousId) &&
              string.Equals(sessao.AnonymousId, anonymousId.Trim(), StringComparison.Ordinal);

    private static PlaybackEventType Traduzir(string? tipo) => (tipo ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "start" => PlaybackEventType.Start,
        "pause" => PlaybackEventType.Pause,
        "seek" => PlaybackEventType.Seek,
        "ended" => PlaybackEventType.Ended,
        "quality" => PlaybackEventType.Quality,
        "error" => PlaybackEventType.Error,
        _ => PlaybackEventType.Progress
    };
}
