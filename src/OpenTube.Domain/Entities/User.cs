using OpenTube.Domain.ValueObjects;

namespace OpenTube.Domain.Entities;

/// <summary>
/// Pessoa conhecida pelo sistema. Convidados viram usuários no primeiro acesso; não há senha
/// em lugar nenhum — a identidade é provada por código enviado ao email.
/// </summary>
public class User
{
    private User() { }

    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string? DisplayName { get; private set; }
    public bool IsAdmin { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastSeenAt { get; private set; }
    public DateTimeOffset? DisabledAt { get; private set; }

    /// <summary>Domínio do email, mantido em coluna própria para consultas por domínio.</summary>
    public string EmailDomain { get; private set; } = string.Empty;

    public bool IsActive => DisabledAt is null;

    public static User Create(EmailAddress email, DateTimeOffset now, bool isAdmin = false, string? displayName = null) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Email = email.Value,
            EmailDomain = email.Domain,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            IsAdmin = isAdmin,
            CreatedAt = now
        };

    public void Touch(DateTimeOffset now) => LastSeenAt = now;

    public void Disable(DateTimeOffset now)
    {
        if (IsAdmin)
            throw new InvalidOperationException("Um administrador não pode ser desativado sem antes perder o papel.");

        DisabledAt ??= now;
    }

    public void Enable() => DisabledAt = null;

    public void GrantAdmin() => IsAdmin = true;

    public void RevokeAdmin() => IsAdmin = false;

    public void Rename(string? displayName) =>
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
}
