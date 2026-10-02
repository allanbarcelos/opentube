// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Queue;

/// <summary>Fila apoiada na tabela <c>processing_jobs</c> do próprio PostgreSQL.</summary>
public class PostgresJobQueue(OpenTubeDbContext db, TimeProvider clock) : IJobQueue
{
    public async Task<Guid> EnqueueAsync(JobKind kind, Guid? targetId = null, object? payload = null, TimeSpan? delay = null, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var json = payload is null ? "{}" : JsonSerializer.Serialize(payload, JsonDefaults.Options);
        var job = ProcessingJob.Create(kind, now, targetId, json, delay);

        db.ProcessingJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);

        return job.Id;
    }

    public async Task<QueuedJob?> DequeueAsync(string workerId, IReadOnlyCollection<JobKind> kinds, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentNullException.ThrowIfNull(kinds);

        if (kinds.Count == 0)
            return null;

        var now = clock.GetUtcNow();
        var connection = db.Database.GetDbConnection();

        // A condição inclui os trabalhos cuja reserva venceu: se um worker morreu no meio,
        // o trabalho precisa voltar a ficar disponível sem intervenção manual. Mas um trabalho
        // que derruba o worker nunca chega a relatar a falha, e sem limite seria resgatado para
        // sempre: esgotadas as tentativas, a reserva vencida o encerra como falho.
        const string sql = """
            WITH esgotado AS (
                UPDATE processing_jobs
                   SET status = @Failed,
                       completed_at = @Now,
                       locked_by = NULL,
                       locked_until = NULL,
                       last_error = @Abandonado
                 WHERE kind = ANY(@Kinds)
                   AND status = @Running
                   AND locked_until IS NOT NULL AND locked_until < @Now
                   AND attempts >= @MaxAttempts
            ),
            escolhido AS (
                SELECT id
                FROM processing_jobs
                WHERE kind = ANY(@Kinds)
                  AND (
                        (status = @Pending AND run_after <= @Now)
                     OR (status = @Running AND locked_until IS NOT NULL AND locked_until < @Now
                         AND attempts < @MaxAttempts)
                  )
                ORDER BY run_after
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE processing_jobs j
               SET status = @Running,
                   attempts = j.attempts + 1,
                   locked_by = @WorkerId,
                   locked_until = @LockedUntil,
                   started_at = COALESCE(j.started_at, @Now)
              FROM escolhido
             WHERE j.id = escolhido.id
            RETURNING j.id, j.kind, j.target_id, j.payload, j.attempts;
            """;

        var row = await connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition(sql, new
        {
            Kinds = kinds.Select(k => (int)k).ToArray(),
            Pending = (int)JobStatus.Pending,
            Running = (int)JobStatus.Running,
            Failed = (int)JobStatus.Failed,
            MaxAttempts = ProcessingJob.MaxAttempts,
            Abandonado = "The worker stopped before finishing the last attempt.",
            Now = now,
            WorkerId = workerId,
            LockedUntil = now + lease
        }, cancellationToken: cancellationToken));

        return row is null
            ? null
            : new QueuedJob(row.id, (JobKind)row.kind, row.target_id, row.payload, row.attempts);
    }

    public async Task<bool> RenewAsync(Guid jobId, string workerId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var connection = db.Database.GetDbConnection();

        const string sql = """
            UPDATE processing_jobs
               SET locked_until = @LockedUntil
             WHERE id = @JobId AND locked_by = @WorkerId AND status = @Running;
            """;

        var affected = await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            JobId = jobId,
            WorkerId = workerId,
            Running = (int)JobStatus.Running,
            LockedUntil = now + lease
        }, cancellationToken: cancellationToken));

        return affected == 1;
    }

    public async Task CompleteAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var connection = db.Database.GetDbConnection();

        const string sql = """
            UPDATE processing_jobs
               SET status = @Succeeded, completed_at = @Now, locked_by = NULL,
                   locked_until = NULL, last_error = NULL
             WHERE id = @JobId;
            """;

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            JobId = jobId,
            Succeeded = (int)JobStatus.Succeeded,
            Now = now
        }, cancellationToken: cancellationToken));
    }

    public async Task FailAsync(Guid jobId, string error, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var connection = db.Database.GetDbConnection();

        // O recuo é calculado no banco a partir da tentativa atual, para que dois workers
        // relatando falha ao mesmo tempo não se sobreponham com valores divergentes.
        const string sql = """
            UPDATE processing_jobs
               SET last_error = LEFT(@Error, 4000),
                   locked_by = NULL,
                   locked_until = NULL,
                   status = CASE WHEN attempts >= @MaxAttempts THEN @Failed ELSE @Pending END,
                   completed_at = CASE WHEN attempts >= @MaxAttempts THEN @Now ELSE NULL END,
                   run_after = CASE WHEN attempts >= @MaxAttempts
                                    THEN run_after
                                    ELSE @Now + (@BaseSeconds * POWER(4, GREATEST(attempts - 1, 0))) * INTERVAL '1 second'
                               END
             WHERE id = @JobId;
            """;

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            JobId = jobId,
            Error = error ?? string.Empty,
            MaxAttempts = ProcessingJob.MaxAttempts,
            Failed = (int)JobStatus.Failed,
            Pending = (int)JobStatus.Pending,
            Now = now,
            BaseSeconds = 30d
        }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyDictionary<JobStatus, int>> CountByStatusAsync(CancellationToken cancellationToken = default)
    {
        var counts = await db.ProcessingJobs
            .GroupBy(j => j.Status)
            .Select(g => new { g.Key, Total = g.Count() })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(c => c.Key, c => c.Total);
    }

    private sealed record JobRow(Guid id, int kind, Guid? target_id, string payload, int attempts);
}
