// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Shared.Analytics;

/// <summary>Um evento relatado pelo player.</summary>
/// <param name="Tipo">start, progress, pause, seek, ended, quality ou error.</param>
/// <param name="Em">Posição no vídeo, em segundos.</param>
/// <param name="De">Começo do trecho assistido desde o relato anterior.</param>
/// <param name="Ate">Fim do trecho assistido desde o relato anterior.</param>
/// <param name="Detalhe">Qualidade escolhida ou descrição do erro.</param>
public sealed record PlaybackEventReport(string Tipo, double Em, double? De = null, double? Ate = null, string? Detalhe = null);

/// <summary>Lote de eventos enviado pelo player em uma batida.</summary>
/// <param name="Eventos">Eventos acumulados desde o envio anterior.</param>
public sealed record PlaybackBatch(PlaybackEventReport[] Eventos);
