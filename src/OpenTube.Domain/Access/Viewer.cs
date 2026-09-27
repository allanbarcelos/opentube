// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

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

    /// <summary>
    /// Concessão por link secreto que o espectador comprovou possuir. Guardamos a concessão
    /// já conferida, e não o token: assim a comparação do segredo fica num só lugar, fora das
    /// regras de acesso.
    /// </summary>
    public Guid? LinkGrantId { get; private init; }

    /// <summary>
    /// Reproduções já iniciadas, cuja visualização já foi contada. Permitem que a reprodução
    /// que consumiu a última visualização de uma concessão chegue até o fim.
    /// </summary>
    public IReadOnlyCollection<ViewInProgress> ViewsInProgress { get; private init; } = [];

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

    /// <summary>Visitante que chegou por um link secreto já conferido, sem se identificar.</summary>
    public static Viewer WithLink(Guid linkGrantId) =>
        new() { LinkGrantId = linkGrantId == Guid.Empty ? null : linkGrantId };

    /// <summary>Acrescenta uma reprodução já iniciada, comprovada fora daqui.</summary>
    public Viewer ContinuingView(Guid videoId, Guid grantId) =>
        this with { ViewsInProgress = [.. ViewsInProgress, new ViewInProgress(videoId, grantId)] };

    /// <summary>
    /// O mesmo espectador pedindo uma reprodução nova, que precisa caber no teto de
    /// visualizações sem se apoiar numa reprodução anterior.
    /// </summary>
    public Viewer StartingNewView() =>
        ViewsInProgress.Count == 0 ? this : this with { ViewsInProgress = [] };

    public bool IsContinuing(Guid videoId, Guid grantId) =>
        ViewsInProgress.Contains(new ViewInProgress(videoId, grantId));

    /// <summary>Acrescenta a concessão por link a quem já está identificado.</summary>
    public Viewer PresentingLink(Guid? linkGrantId) =>
        this with { LinkGrantId = linkGrantId is null || linkGrantId == Guid.Empty ? LinkGrantId : linkGrantId };
}

/// <summary>Reprodução em andamento de um vídeo, sustentada por uma concessão.</summary>
/// <param name="VideoId">Vídeo sendo assistido.</param>
/// <param name="GrantId">Concessão que teve a visualização contada.</param>
public readonly record struct ViewInProgress(Guid VideoId, Guid GrantId);
