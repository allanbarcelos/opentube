// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Entities;

/// <summary>
/// Um pedido de código registrado para a janela curta do mesmo email.
/// </summary>
public class AuthAttempt
{
    private AuthAttempt() { }

    public long Id { get; private set; }

    /// <summary>Chave do balde: o email de quem pediu o código.</summary>
    public string Scope { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }

    public static AuthAttempt Record(string scope, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        return new AuthAttempt { Scope = scope, OccurredAt = now };
    }
}
