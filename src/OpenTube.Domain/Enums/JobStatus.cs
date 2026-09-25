namespace OpenTube.Domain.Enums;

/// <summary>Estado de um item da fila de processamento.</summary>
public enum JobStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4
}
