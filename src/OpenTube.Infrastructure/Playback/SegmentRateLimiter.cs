// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTube.Infrastructure.Options;

namespace OpenTube.Infrastructure.Playback;

/// <summary>
/// Limita a velocidade com que cada pessoa busca os segmentos de um vídeo. O player assiste
/// perto do tempo real, com algum adiantamento; um programa que baixa o vídeo pede tudo de uma
/// vez. Com o limite, baixar leva quase tanto quanto assistir, e a tentativa fica no log.
/// </summary>
/// <remarks>
/// Balde de fichas por pessoa e vídeo: começa cheio, com folga para o carregamento inicial e
/// para pular de um ponto a outro, e se repõe numa taxa acima da velocidade do vídeo. Fica em
/// memória: a aplicação roda numa réplica só, e um reinício só devolve a folga inicial.
/// </remarks>
public class SegmentRateLimiter(IOptions<SecurityOptions> options, TimeProvider clock, ILogger<SegmentRateLimiter> logger)
{
    /// <summary>Balde sem uso há mais que isso é descartado.</summary>
    private static readonly TimeSpan Abandono = TimeSpan.FromHours(1);

    /// <summary>Intervalo mínimo entre dois avisos no log para a mesma pessoa e vídeo.</summary>
    private static readonly TimeSpan IntervaloDoAviso = TimeSpan.FromMinutes(1);

    private readonly SecurityOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, Balde> _baldes = new(StringComparer.Ordinal);
    private DateTimeOffset _ultimaFaxina = DateTimeOffset.MinValue;

    /// <summary>Consome uma ficha. Devolve <c>false</c> quando a pessoa está buscando rápido demais.</summary>
    public bool TryAcquire(string viewerKey, Guid videoId)
    {
        if (_options.SegmentsPerSecond <= 0 || _options.SegmentBurst <= 0)
            return true;

        var agora = clock.GetUtcNow();
        Faxinar(agora);

        var chave = viewerKey + "|" + videoId.ToString("n");
        var balde = _baldes.GetOrAdd(chave, _ => new Balde(_options.SegmentBurst, agora));

        lock (balde)
        {
            var decorrido = (agora - balde.Atualizado).TotalSeconds;
            balde.Fichas = Math.Min(_options.SegmentBurst, balde.Fichas + Math.Max(0, decorrido) * _options.SegmentsPerSecond);
            balde.Atualizado = agora;

            if (balde.Fichas >= 1)
            {
                balde.Fichas -= 1;
                return true;
            }

            if (agora - balde.Avisado >= IntervaloDoAviso)
            {
                balde.Avisado = agora;
                logger.LogWarning(
                    "Segmentos do vídeo {VideoId} pedidos rápido demais por {Espectador}: possível download",
                    videoId, viewerKey);
            }

            return false;
        }
    }

    private void Faxinar(DateTimeOffset agora)
    {
        if (agora - _ultimaFaxina < Abandono)
            return;

        _ultimaFaxina = agora;

        foreach (var (chave, balde) in _baldes)
        {
            if (agora - balde.Atualizado > Abandono)
                _baldes.TryRemove(chave, out _);
        }
    }

    private sealed class Balde(double fichas, DateTimeOffset atualizado)
    {
        public double Fichas { get; set; } = fichas;
        public DateTimeOffset Atualizado { get; set; } = atualizado;
        public DateTimeOffset Avisado { get; set; } = DateTimeOffset.MinValue;
    }
}
