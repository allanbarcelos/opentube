using OpenTube.Domain.Enums;

namespace OpenTube.Infrastructure.Queue;

/// <summary>
/// Fila de trabalhos assíncronos. A implementação vive no próprio PostgreSQL: com
/// <c>FOR UPDATE SKIP LOCKED</c> vários workers consomem em paralelo sem trabalho duplicado,
/// e nenhum intermediário precisa ser operado só para isso.
/// </summary>
public interface IJobQueue
{
    /// <summary>Coloca um trabalho na fila, opcionalmente para daqui a algum tempo.</summary>
    Task<Guid> EnqueueAsync(JobKind kind, Guid? targetId = null, object? payload = null, TimeSpan? delay = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retira o próximo trabalho disponível de um dos tipos informados, reservando-o pelo prazo
    /// indicado. Devolve <c>null</c> quando não há nada a fazer.
    /// </summary>
    Task<QueuedJob?> DequeueAsync(string workerId, IReadOnlyCollection<JobKind> kinds, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>Estende a reserva de um trabalho em andamento.</summary>
    Task<bool> RenewAsync(Guid jobId, string workerId, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>Marca o trabalho como concluído.</summary>
    Task CompleteAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registra a falha. Havendo tentativas restantes o trabalho volta para a fila com recuo
    /// exponencial; esgotadas, fica marcado como falho para inspeção.
    /// </summary>
    Task FailAsync(Guid jobId, string error, CancellationToken cancellationToken = default);

    /// <summary>Quantos trabalhos de cada estado existem — usado no painel administrativo.</summary>
    Task<IReadOnlyDictionary<JobStatus, int>> CountByStatusAsync(CancellationToken cancellationToken = default);
}
