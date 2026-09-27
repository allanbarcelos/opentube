// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Analytics;
using OpenTube.Infrastructure.Queue;

namespace OpenTube.Worker.Jobs;

/// <summary>Ajustes da manutenção periódica do analytics.</summary>
public class AnalyticsOptions
{
    public const string SectionName = "Analytics";

    /// <summary>Intervalo entre agregações.</summary>
    public TimeSpan RollupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Quantos dias são recalculados a cada passagem.</summary>
    public int RollupDays { get; set; } = 2;

    /// <summary>
    /// Por quanto tempo o evento bruto é guardado. Os agregados permanecem; o que se perde
    /// depois desse prazo é a investigação de um caso pontual.
    /// </summary>
    public TimeSpan EventRetention { get; set; } = TimeSpan.FromDays(730);
}

/// <summary>
/// Manutenção periódica do analytics: fecha sessões sem sinal, consolida os números do
/// período, garante as partições do mês seguinte e descarta evento bruto vencido. O trabalho
/// se reagenda ao terminar, o que dispensa um agendador à parte.
/// </summary>
public class AnalyticsRollupJobHandler(
    AnalyticsCollector coletor,
    AnalyticsAggregator agregador,
    IJobQueue fila,
    IOptions<AnalyticsOptions> options,
    TimeProvider clock,
    ILogger<AnalyticsRollupJobHandler> logger) : IJobHandler
{
    private readonly AnalyticsOptions _options = options.Value;

    public JobKind Kind => JobKind.AnalyticsRollup;

    public async Task HandleAsync(QueuedJob job, CancellationToken cancellationToken = default)
    {
        try
        {
            var fechadas = await coletor.CloseStaleAsync(cancellationToken);
            var resultado = await agregador.RollupRecentAsync(_options.RollupDays, cancellationToken);

            await coletor.EnsurePartitionsAsync(clock.GetUtcNow(), cancellationToken);

            var descartados = await agregador.PruneEventsAsync(_options.EventRetention, cancellationToken);

            logger.LogInformation(
                "Analytics: {Fechadas} sessões encerradas, {Videos} vídeos agregados, {Descartados} eventos descartados",
                fechadas, resultado.Videos, descartados);
        }
        finally
        {
            // Reagenda mesmo em caso de falha: uma agregação que quebrou hoje não pode
            // deixar o painel desatualizado para sempre.
            await fila.EnqueueAsync(
                JobKind.AnalyticsRollup,
                delay: _options.RollupInterval,
                cancellationToken: CancellationToken.None);
        }
    }
}
