// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Entities;

/// <summary>
/// Uma tentativa registrada para fins de limitação de taxa. Sem senha no sistema, o limite de
/// pedidos e de digitações é a única barreira contra a força bruta num código de seis dígitos.
/// </summary>
public class AuthAttempt
{
    private AuthAttempt() { }

    public long Id { get; private set; }

    /// <summary>Chave do balde: o email, o domínio ou o resumo do endereço de origem.</summary>
    public string Scope { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }

    public static AuthAttempt Record(string scope, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        return new AuthAttempt { Scope = scope, OccurredAt = now };
    }
}
