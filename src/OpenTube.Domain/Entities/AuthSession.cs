namespace OpenTube.Domain.Entities;

/// <summary>
/// Sessão ativa de uma pessoa. Fica no banco para que o administrador possa encerrar o acesso
/// de alguém na hora, sem esperar o cookie expirar.
/// </summary>
public class AuthSession
{
    private AuthSession() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public string? IpHash { get; private set; }
    public string? UserAgent { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    public bool IsValidAt(DateTimeOffset now) => !IsRevoked && now < ExpiresAt;

    public static AuthSession Open(Guid userId, DateTimeOffset now, TimeSpan lifetime, string? ipHash = null, string? userAgent = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        return new AuthSession
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
            LastSeenAt = now,
            IpHash = ipHash,
            UserAgent = Truncate(userAgent, 400)
        };
    }

    /// <summary>
    /// Registra atividade e estende a validade, para que quem usa o sistema com frequência
    /// não precise pedir um código novo a cada trinta dias.
    /// </summary>
    public void Touch(DateTimeOffset now, TimeSpan lifetime)
    {
        if (!IsValidAt(now))
            throw new InvalidOperationException("Não é possível renovar uma sessão encerrada.");

        LastSeenAt = now;
        ExpiresAt = now + lifetime;
    }

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}
