// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Enums;

namespace OpenTube.Domain.Entities;

/// <summary>
/// Um convite: o conjunto de concessões criadas de uma vez, com a mesma validade — várias
/// pessoas, vários domínios, ou um link secreto. Cada convite é independente: criar um novo
/// nunca mexe nas concessões de outro, e a mesma pessoa em dois convites tem os dois acessos.
/// </summary>
public class Invitation
{
    private Invitation() { }

    /// <summary>A nota é um lembrete curto, mostrado ao lado de cada acesso na lista.</summary>
    public const int MaxNoteLength = 64;

    public Guid Id { get; private set; }
    public InvitationKind Kind { get; private set; }
    public GrantTargetType TargetType { get; private set; }
    public Guid? TargetId { get; private set; }

    /// <summary>Validade comum a todos do convite, como foi escolhida.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public TimeSpan? DurationAfterFirstUse { get; private set; }

    /// <summary>Teto de visualizações do link secreto.</summary>
    public int? MaxViews { get; private set; }

    public string? Note { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Quando o convite inteiro foi revogado. As concessões dele são revogadas no mesmo
    /// instante, o que permite restaurar só essas depois — e não as que alguém já tinha
    /// revogado uma a uma antes.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    public static Invitation Create(
        InvitationKind kind,
        GrantTargetType targetType,
        Guid? targetId,
        Guid createdBy,
        DateTimeOffset now,
        DateTimeOffset? expiresAt = null,
        TimeSpan? durationAfterFirstUse = null,
        int? maxViews = null,
        string? note = null)
    {
        if (targetType is GrantTargetType.All && targetId is not null)
            throw new ArgumentException("A grant for the whole library does not point at a target.", nameof(targetId));

        if (targetType is not GrantTargetType.All && (targetId is null || targetId == Guid.Empty))
            throw new ArgumentException("Name the video or collection this grant applies to.", nameof(targetId));

        if (maxViews is not null && kind is not InvitationKind.Link)
            throw new ArgumentException("Only a secret link has a view limit.", nameof(maxViews));

        if (note is not null && note.Trim().Length > MaxNoteLength)
            throw new ArgumentException("The note can have at most 64 characters.", nameof(note));

        return new Invitation
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            TargetType = targetType,
            TargetId = targetId,
            ExpiresAt = expiresAt,
            DurationAfterFirstUse = durationAfterFirstUse,
            MaxViews = maxViews,
            CreatedBy = createdBy,
            CreatedAt = now,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
        };
    }

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    public void Restore() => RevokedAt = null;
}
