// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Entities;

/// <summary>
/// Uma ação administrativa registrada. É o que sustenta a resposta à pergunta "quem liberou
/// esse vídeo?" meses depois, quando ninguém mais lembra.
/// </summary>
public class AuditEntry
{
    private AuditEntry() { }

    public long Id { get; private set; }

    /// <summary>Quem fez. Nulo quando a ação partiu do próprio sistema.</summary>
    public Guid? ActorId { get; private set; }

    /// <summary>Endereço de quem fez, preservado mesmo que a conta suma depois.</summary>
    public string ActorEmail { get; private set; } = "sistema";

    /// <summary>O que foi feito, em forma de código estável.</summary>
    public string Action { get; private set; } = string.Empty;

    /// <summary>Sobre o que recaiu: vídeo, coleção, concessão, domínio, pessoa.</summary>
    public string EntityType { get; private set; } = string.Empty;

    public Guid? EntityId { get; private set; }

    /// <summary>Descrição em português, para ser lida sem consultar o código.</summary>
    public string Summary { get; private set; } = string.Empty;

    /// <summary>Resumo do endereço de origem.</summary>
    public string? IpHash { get; private set; }

    public DateTimeOffset At { get; private set; }

    public static AuditEntry Record(
        Guid? actorId,
        string? actorEmail,
        string action,
        string entityType,
        Guid? entityId,
        string summary,
        DateTimeOffset now,
        string? ipHash = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        return new AuditEntry
        {
            ActorId = actorId,
            ActorEmail = string.IsNullOrWhiteSpace(actorEmail) ? "sistema" : actorEmail.Trim().ToLowerInvariant(),
            Action = action.Trim(),
            EntityType = entityType.Trim(),
            EntityId = entityId,
            Summary = Truncate(summary.Trim(), 500),
            At = now,
            IpHash = ipHash
        };
    }

    private static string Truncate(string valor, int maximo) =>
        valor.Length <= maximo ? valor : valor[..maximo];
}
