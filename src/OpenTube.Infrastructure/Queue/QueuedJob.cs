using OpenTube.Domain.Enums;

namespace OpenTube.Infrastructure.Queue;

/// <summary>
/// Trabalho retirado da fila, no formato mínimo de que o worker precisa. Não é a entidade
/// completa de propósito: o worker não deve alterar o registro fora dos métodos da fila.
/// </summary>
/// <param name="Id">Identificador do job.</param>
/// <param name="Kind">Tipo de trabalho.</param>
/// <param name="TargetId">Entidade alvo, normalmente o vídeo.</param>
/// <param name="Payload">Parâmetros em JSON.</param>
/// <param name="Attempts">Tentativa atual, começando em 1.</param>
public sealed record QueuedJob(Guid Id, JobKind Kind, Guid? TargetId, string Payload, int Attempts)
{
    /// <summary>Lê o payload no tipo esperado pelo executor.</summary>
    public T? PayloadAs<T>() =>
        string.IsNullOrWhiteSpace(Payload)
            ? default
            : System.Text.Json.JsonSerializer.Deserialize<T>(Payload, JsonDefaults.Options);
}

internal static class JsonDefaults
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);
}
