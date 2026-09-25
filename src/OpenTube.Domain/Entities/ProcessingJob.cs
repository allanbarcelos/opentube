using OpenTube.Domain.Enums;

namespace OpenTube.Domain.Entities;

/// <summary>
/// Item da fila de processamento. A fila mora no PostgreSQL e é consumida com
/// <c>FOR UPDATE SKIP LOCKED</c>, o que evita trazer um intermediário só para isso.
/// </summary>
public class ProcessingJob
{
    /// <summary>Quantas vezes um job tenta antes de ser dado como perdido.</summary>
    public const int MaxAttempts = 3;

    private ProcessingJob() { }

    public Guid Id { get; private set; }
    public JobKind Kind { get; private set; }
    public JobStatus Status { get; private set; }

    /// <summary>Entidade alvo do trabalho — normalmente o vídeo.</summary>
    public Guid? TargetId { get; private set; }

    /// <summary>Parâmetros do trabalho, serializados como JSON.</summary>
    public string Payload { get; private set; } = "{}";

    public int Attempts { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Momento a partir do qual o job pode ser retirado da fila (usado no recuo exponencial).</summary>
    public DateTimeOffset RunAfter { get; private set; }

    public string? LockedBy { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public static ProcessingJob Create(JobKind kind, DateTimeOffset now, Guid? targetId = null, string? payload = null, TimeSpan? delay = null) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            Status = JobStatus.Pending,
            TargetId = targetId,
            Payload = string.IsNullOrWhiteSpace(payload) ? "{}" : payload,
            RunAfter = now + (delay ?? TimeSpan.Zero),
            CreatedAt = now
        };

    /// <summary>Marca o job como em execução por um worker, com validade de reserva.</summary>
    public void Start(string workerId, DateTimeOffset now, TimeSpan lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        Status = JobStatus.Running;
        Attempts++;
        LockedBy = workerId;
        LockedUntil = now + lease;
        StartedAt ??= now;
    }

    /// <summary>Estende a reserva enquanto o trabalho ainda está em andamento.</summary>
    public void Renew(DateTimeOffset now, TimeSpan lease)
    {
        if (Status is not JobStatus.Running)
            throw new InvalidOperationException("Só um job em execução pode ter a reserva estendida.");

        LockedUntil = now + lease;
    }

    public void Succeed(DateTimeOffset now)
    {
        Status = JobStatus.Succeeded;
        CompletedAt = now;
        LockedBy = null;
        LockedUntil = null;
        LastError = null;
    }

    /// <summary>
    /// Registra a falha. Enquanto houver tentativas restantes o job volta para a fila com
    /// recuo exponencial; esgotadas, fica como falho para inspeção manual.
    /// </summary>
    public void Fail(string error, DateTimeOffset now)
    {
        LastError = Truncate(error, 4000);
        LockedBy = null;
        LockedUntil = null;

        if (Attempts >= MaxAttempts)
        {
            Status = JobStatus.Failed;
            CompletedAt = now;
            return;
        }

        Status = JobStatus.Pending;
        RunAfter = now + BackoffFor(Attempts);
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status is JobStatus.Succeeded)
            throw new InvalidOperationException("Um job já concluído não pode ser cancelado.");

        Status = JobStatus.Cancelled;
        CompletedAt = now;
        LockedBy = null;
        LockedUntil = null;
    }

    /// <summary>Recuo exponencial entre tentativas: 30 s, 2 min, 8 min…</summary>
    public static TimeSpan BackoffFor(int attempts) =>
        TimeSpan.FromSeconds(30 * Math.Pow(4, Math.Max(0, attempts - 1)));

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
