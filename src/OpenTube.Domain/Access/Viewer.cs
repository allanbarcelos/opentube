using OpenTube.Domain.Entities;
using OpenTube.Domain.ValueObjects;

namespace OpenTube.Domain.Access;

/// <summary>
/// Quem está pedindo para assistir. Pode ser um visitante anônimo (só enxerga o que é público),
/// um convidado autenticado ou um administrador.
/// </summary>
public sealed record Viewer
{
    public static readonly Viewer Anonymous = new();

    private Viewer() { }

    public Guid? UserId { get; private init; }
    public string? Email { get; private init; }
    public string? EmailDomain { get; private init; }
    public bool IsAdmin { get; private init; }

    /// <summary>Token de link secreto apresentado na requisição, quando houver.</summary>
    public string? LinkToken { get; private init; }

    public bool IsAuthenticated => UserId is not null;

    public static Viewer Authenticated(Guid userId, EmailAddress email, bool isAdmin = false) =>
        new()
        {
            UserId = userId,
            Email = email.Value,
            EmailDomain = email.Domain,
            IsAdmin = isAdmin
        };

    public static Viewer From(User user) =>
        Authenticated(user.Id, EmailAddress.Parse(user.Email), user.IsAdmin);

    /// <summary>Visitante que chegou por um link secreto, sem se identificar.</summary>
    public static Viewer WithLink(string linkToken) =>
        new() { LinkToken = string.IsNullOrWhiteSpace(linkToken) ? null : linkToken.Trim() };

    /// <summary>Acrescenta um token de link a quem já está identificado.</summary>
    public Viewer PresentingLink(string? linkToken) =>
        this with { LinkToken = string.IsNullOrWhiteSpace(linkToken) ? LinkToken : linkToken.Trim() };
}
