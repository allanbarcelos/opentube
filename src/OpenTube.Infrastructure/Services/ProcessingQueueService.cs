// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Services;

/// <summary>Um trabalho da fila, com o que a administração precisa para decidir sobre ele.</summary>
/// <param name="Id">Trabalho.</param>
/// <param name="Kind">Tipo.</param>
/// <param name="Status">Situação.</param>
/// <param name="VideoId">Vídeo a que o trabalho se refere, quando se refere a um.</param>
/// <param name="VideoTitle">Título do vídeo.</param>
/// <param name="Language">Idioma, num trabalho de legenda.</param>
/// <param name="Attempts">Tentativas feitas.</param>
/// <param name="LastError">Motivo da última falha.</param>
/// <param name="CreatedAt">Quando entrou na fila.</param>
/// <param name="RunAfter">A partir de quando pode rodar (agendado, ou esperando nova tentativa).</param>
/// <param name="StartedAt">Quando começou.</param>
/// <param name="LockedBy">Worker que está com ele.</param>
/// <param name="CanCancel">Se a administração pode cancelá-lo.</param>
public sealed record QueueItem(
    Guid Id,
    JobKind Kind,
    JobStatus Status,
    Guid? VideoId,
    string? VideoTitle,
    string? Language,
    int Attempts,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset RunAfter,
    DateTimeOffset? StartedAt,
    string? LockedBy,
    bool CanCancel);

/// <summary>
/// A fila de processamento vista pela administração: o que está esperando, rodando ou falhou,
/// e o cancelamento do que ainda não começou.
/// </summary>
public class ProcessingQueueService(OpenTubeDbContext db, TimeProvider clock, ILogger<ProcessingQueueService> logger)
{
    /// <summary>Quantos trabalhos uma listagem traz, no máximo.</summary>
    public const int ListLimit = 200;

    /// <summary>
    /// Só o trabalho pedido por alguém pode ser cancelado. Os de manutenção se reagendam
    /// sozinhos (a agregação da audiência, a cada 5 minutos) ou limpam arquivos antigos:
    /// cancelá-los quebraria o ciclo ou deixaria lixo no storage.
    /// </summary>
    public static bool IsCancellable(JobKind kind) => kind is JobKind.Transcode or JobKind.Transcript;

    public async Task<IReadOnlyList<QueueItem>> ListAsync(JobStatus status, CancellationToken cancellationToken = default)
    {
        var consulta = db.ProcessingJobs.AsNoTracking().Where(j => j.Status == status);

        // A fila na ordem em que vai andar; o resto, do mais recente para o mais antigo.
        consulta = status is JobStatus.Pending
            ? consulta.OrderBy(j => j.RunAfter).ThenBy(j => j.CreatedAt)
            : consulta.OrderByDescending(j => j.StartedAt ?? j.CreatedAt);

        var trabalhos = await consulta
            .Take(ListLimit)
            .Select(j => new
            {
                j.Id, j.Kind, j.Status, j.TargetId, j.Payload, j.Attempts, j.LastError,
                j.CreatedAt, j.RunAfter, j.StartedAt, j.LockedBy
            })
            .ToListAsync(cancellationToken);

        var videoIds = trabalhos.Where(j => j.TargetId is not null).Select(j => j.TargetId!.Value).Distinct().ToList();
        var titulos = await db.Videos
            .AsNoTracking()
            .Where(v => videoIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => v.Title, cancellationToken);

        return [.. trabalhos.Select(j => new QueueItem(
            j.Id,
            j.Kind,
            j.Status,
            j.TargetId is { } alvo && titulos.ContainsKey(alvo) ? alvo : null,
            j.TargetId is { } id && titulos.TryGetValue(id, out var titulo) ? titulo : null,
            j.Kind is JobKind.Transcript ? LerTexto(j.Payload, "language") : null,
            j.Attempts,
            j.LastError,
            j.CreatedAt,
            j.RunAfter,
            j.StartedAt,
            j.LockedBy,
            j.Status is JobStatus.Pending && IsCancellable(j.Kind)))];
    }

    /// <summary>
    /// Cancela um trabalho que ainda não começou, e deixa o que ele ia produzir coerente: o vídeo
    /// que esperava a primeira transcodificação passa a falho (pode ser reprocessado), e a
    /// legenda que esperava a transcrição passa a falha, com o motivo.
    /// </summary>
    /// <returns>O vídeo do trabalho, para a auditoria.</returns>
    public async Task<Guid?> CancelAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var agora = clock.GetUtcNow();

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);

        var trabalho = await db.ProcessingJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
            ?? throw new InvalidOperationException("This job is no longer in the queue.");

        if (!IsCancellable(trabalho.Kind))
            throw new InvalidOperationException("Maintenance jobs run on their own and cannot be cancelled.");

        // Condicional: se o worker o pegou entre a tela e o clique, o cancelamento não vale.
        var cancelados = await db.ProcessingJobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Cancelled)
                .SetProperty(j => j.CompletedAt, agora), cancellationToken);

        if (cancelados == 0)
            throw new InvalidOperationException("This job has already started or finished and can no longer be cancelled.");

        if (trabalho.Kind is JobKind.Transcode && trabalho.TargetId is { } videoId)
        {
            var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, cancellationToken);

            // Um vídeo pronto sendo reprocessado continua pronto, com a versão que já tinha.
            if (video is { Status: VideoStatus.Uploaded })
                video.MarkFailed();
        }

        if (trabalho.Kind is JobKind.Transcript && LerGuid(trabalho.Payload, "assetId") is { } legendaId)
        {
            var legenda = await db.VideoAssets.FirstOrDefaultAsync(
                a => a.Id == legendaId && a.Kind == VideoAssetKind.Caption, cancellationToken);

            if (legenda is { IsProcessing: true })
                legenda.FailTranscription("Cancelled by an administrator.", agora);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transacao.CommitAsync(cancellationToken);

        logger.LogInformation("Trabalho {JobId} ({Tipo}) cancelado pela administração", jobId, trabalho.Kind);

        return trabalho.TargetId;
    }

    /// <summary>Tira um trabalho que falhou da lista de falhas, depois de visto.</summary>
    /// <returns>O vídeo do trabalho, para a auditoria.</returns>
    public async Task<Guid?> DismissAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var trabalho = await db.ProcessingJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
            ?? throw new InvalidOperationException("This job is no longer in the queue.");

        var descartados = await db.ProcessingJobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Failed)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Cancelled), cancellationToken);

        if (descartados == 0)
            throw new InvalidOperationException("Only a failed job can be removed from the list.");

        return trabalho.TargetId;
    }

    private static string? LerTexto(string payload, string campo)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            return json.RootElement.ValueKind is JsonValueKind.Object
                   && json.RootElement.TryGetProperty(campo, out var valor)
                   && valor.ValueKind is JsonValueKind.String
                ? valor.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Guid? LerGuid(string payload, string campo) =>
        Guid.TryParse(LerTexto(payload, campo), out var id) ? id : null;
}
