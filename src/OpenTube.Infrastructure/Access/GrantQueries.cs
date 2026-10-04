// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Access;

/// <summary>Um convite como a administração o vê: a validade comum e cada pessoa, domínio ou link dele.</summary>
/// <param name="Id">Convite; nas concessões de antes dos convites, o da própria concessão.</param>
/// <param name="Kind">Pessoas, domínios ou link.</param>
/// <param name="IsLegacy">Concessão de antes dos convites, mostrada como um convite próprio.</param>
/// <param name="CreatedAt">Quando foi criado.</param>
/// <param name="CreatedBy">Email de quem criou, quando conhecido.</param>
/// <param name="ExpiresAt">Data de término comum.</param>
/// <param name="DurationAfterFirstUse">Prazo a partir do primeiro acesso, comum.</param>
/// <param name="MaxViews">Teto de visualizações (link).</param>
/// <param name="Note">Anotação.</param>
/// <param name="RevokedAt">Quando o convite inteiro foi revogado.</param>
/// <param name="Members">As concessões do convite.</param>
public sealed record InvitationView(
    Guid Id,
    InvitationKind Kind,
    bool IsLegacy,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    DateTimeOffset? ExpiresAt,
    TimeSpan? DurationAfterFirstUse,
    int? MaxViews,
    string? Note,
    DateTimeOffset? RevokedAt,
    IReadOnlyList<AccessGrant> Members)
{
    /// <summary>Se ainda dá acesso a alguém: não revogado e com ao menos uma concessão valendo.</summary>
    public bool IsActiveAt(DateTimeOffset now) => RevokedAt is null && Members.Any(m => m.IsActiveAt(now, ignoreViewLimit: false));
}

/// <summary>
/// Leitura das concessões e dos convites, para os painéis da administração e a auditoria. Não
/// altera nada: conceder e revogar ficam no <see cref="GrantService"/>.
/// </summary>
public class GrantQueries(OpenTubeDbContext db)
{
    /// <summary>
    /// Convites de um alvo, do mais recente para o mais antigo, cada um com as suas concessões.
    /// As concessões de antes dos convites aparecem cada uma como um convite próprio.
    /// </summary>
    public async Task<IReadOnlyList<InvitationView>> ListInvitationsAsync(
        GrantTargetType targetType, Guid? targetId, CancellationToken cancellationToken = default)
    {
        var convites = await db.Invitations
            .AsNoTracking()
            .Where(i => i.TargetType == targetType && i.TargetId == targetId)
            .ToListAsync(cancellationToken);

        var concessoes = await ListForTargetAsync(targetType, targetId, cancellationToken);

        var autores = convites.Select(c => c.CreatedBy).Concat(concessoes.Select(c => c.CreatedBy)).Distinct().ToList();
        var emails = await db.Users
            .AsNoTracking()
            .Where(u => autores.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email, cancellationToken);

        var porConvite = concessoes.Where(c => c.InvitationId is not null).ToLookup(c => c.InvitationId!.Value);

        var lista = convites.Select(c => new InvitationView(
            c.Id, c.Kind, false, c.CreatedAt, emails.GetValueOrDefault(c.CreatedBy),
            c.ExpiresAt, c.DurationAfterFirstUse, c.MaxViews, c.Note, c.RevokedAt,
            [.. porConvite[c.Id].OrderBy(m => m.SubjectValue, StringComparer.Ordinal)]));

        var antigas = concessoes.Where(c => c.InvitationId is null).Select(c => new InvitationView(
            c.Id, TipoDoConvite(c.SubjectType), true, c.CreatedAt, emails.GetValueOrDefault(c.CreatedBy),
            c.ExpiresAt, c.DurationAfterFirstUse, c.MaxViews, c.Note, null, [c]));

        return [.. lista.Concat(antigas).OrderByDescending(c => c.CreatedAt)];
    }

    /// <summary>Concessões que recaem sobre um alvo, da mais recente para a mais antiga.</summary>
    public Task<List<AccessGrant>> ListForTargetAsync(
        GrantTargetType targetType, Guid? targetId, CancellationToken cancellationToken = default) =>
        db.AccessGrants
            .AsNoTracking()
            .Where(g => g.TargetType == targetType && g.TargetId == targetId)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync(cancellationToken);

    /// <summary>Todas as concessões de uma pessoa, usadas no painel por usuário.</summary>
    public Task<List<AccessGrant>> ListForEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalizado = (email ?? string.Empty).Trim().ToLowerInvariant();

        return db.AccessGrants
            .AsNoTracking()
            .Where(g => g.SubjectType == GrantSubjectType.User && g.SubjectValue == normalizado)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Nome do alvo de uma concessão — o título do vídeo ou o nome da coleção. Nulo para o
    /// acervo inteiro ou um alvo que não existe mais.
    /// </summary>
    public async Task<string?> TargetNameAsync(GrantTargetType targetType, Guid? targetId, CancellationToken cancellationToken = default)
    {
        if (targetId is not { } id)
            return null;

        return targetType switch
        {
            GrantTargetType.Video => await db.Videos.Where(v => v.Id == id).Select(v => v.Title).FirstOrDefaultAsync(cancellationToken),
            GrantTargetType.Collection => await db.Collections.Where(c => c.Id == id).Select(c => c.Name).FirstOrDefaultAsync(cancellationToken),
            _ => null
        };
    }

    private static InvitationKind TipoDoConvite(GrantSubjectType tipo) => tipo switch
    {
        GrantSubjectType.Domain => InvitationKind.Domains,
        GrantSubjectType.Link => InvitationKind.Link,
        _ => InvitationKind.People
    };
}
