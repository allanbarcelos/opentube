using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;

namespace OpenTube.Domain.Entities;

/// <summary>
/// Código de acesso de uso único enviado por email. Guardamos apenas o resumo criptográfico:
/// quem tiver acesso ao banco não consegue entrar no lugar de ninguém.
/// </summary>
public class LoginCode
{
    /// <summary>Quantas tentativas de digitação um código aceita antes de ser queimado.</summary>
    public const int MaxAttempts = 5;

    private LoginCode() { }

    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string EmailDomain { get; private set; } = string.Empty;
    public AuthPurpose Purpose { get; private set; }

    /// <summary>Resumo do código de seis dígitos.</summary>
    public string CodeHash { get; private set; } = string.Empty;

    /// <summary>Resumo do token do link de acesso direto.</summary>
    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }
    public int Attempts { get; private set; }

    /// <summary>Resumo do endereço de origem do pedido, para investigar abuso.</summary>
    public string? IpHash { get; private set; }

    /// <summary>Concessão que originou o convite, quando houver.</summary>
    public Guid? GrantId { get; private set; }

    public bool IsConsumed => ConsumedAt is not null;

    public bool IsExhausted => Attempts >= MaxAttempts;

    public bool IsExpiredAt(DateTimeOffset now) => now >= ExpiresAt;

    public bool IsUsableAt(DateTimeOffset now) => !IsConsumed && !IsExhausted && !IsExpiredAt(now);

    public static LoginCode Issue(
        EmailAddress email,
        AuthPurpose purpose,
        string codeHash,
        string tokenHash,
        DateTimeOffset now,
        TimeSpan lifetime,
        string? ipHash = null,
        Guid? grantId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        return new LoginCode
        {
            Id = Guid.CreateVersion7(),
            Email = email.Value,
            EmailDomain = email.Domain,
            Purpose = purpose,
            CodeHash = codeHash,
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
            IpHash = ipHash,
            GrantId = grantId
        };
    }

    /// <summary>Registra uma tentativa de digitação errada.</summary>
    public void RegisterFailedAttempt() => Attempts++;

    /// <summary>Marca o código como usado. Um código só vale uma vez.</summary>
    public void Consume(DateTimeOffset now)
    {
        if (IsConsumed)
            throw new InvalidOperationException("Este código de acesso já foi usado.");

        ConsumedAt = now;
    }
}
