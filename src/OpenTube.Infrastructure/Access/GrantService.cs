// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenTube.Domain.Access;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Localization;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Access;

/// <summary>Resultado do convite de uma pessoa.</summary>
/// <param name="Email">Endereço convidado.</param>
/// <param name="GrantId">Concessão criada.</param>
/// <param name="EmailSent">Se o convite foi despachado.</param>
/// <param name="InvitationId">Convite de que a concessão faz parte.</param>
public sealed record InviteResult(string Email, Guid GrantId, bool EmailSent, Guid InvitationId);

/// <summary>
/// Concede e revoga acesso a pessoas e domínios. Os links secretos ficam no
/// <see cref="ShareLinkService"/>, a leitura no <see cref="GrantQueries"/> e o email de convite
/// no <see cref="InvitationMailer"/>.
/// </summary>
public class GrantService(
    OpenTubeDbContext db,
    InvitationMailer mailer,
    TimeProvider clock,
    ILogger<GrantService> logger)
{
    /// <summary>
    /// Convida uma ou mais pessoas, com a mesma validade, e envia o email a cada uma. É sempre
    /// um convite novo: nenhuma concessão que já exista é alterada, e quem já tinha acesso por
    /// outro convite passa a ter os dois.
    /// </summary>
    public async Task<IReadOnlyList<InviteResult>> InviteAsync(
        IEnumerable<string> emails,
        GrantTargetType targetType,
        Guid? targetId,
        GrantValidity validity,
        Guid adminId,
        string? note = null,
        bool sendEmail = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emails);

        var enderecos = emails
            .Select(e => EmailAddress.TryParse(e, out var endereco) ? endereco : (EmailAddress?)null)
            .Where(e => e is not null)
            .Select(e => e!.Value)
            .DistinctBy(e => e.Value)
            .ToList();

        if (enderecos.Count == 0)
            throw new InvalidOperationException("No valid email address was given.");

        var convite = Invitation.Create(InvitationKind.People, targetType, targetId, adminId, clock.GetUtcNow(),
            validity.ExpiresAt, validity.DurationAfterFirstUse, note: note);
        var concessoes = enderecos
            .Select(e => (Endereco: e, Concessao: AccessGrant.ForInvitation(convite, GrantSubjectType.User, e.Value)))
            .ToList();

        db.Invitations.Add(convite);
        db.AccessGrants.AddRange(concessoes.Select(c => c.Concessao));
        await db.SaveChangesAsync(cancellationToken);

        if (sendEmail)
            await mailer.SendAsync(enderecos, targetType, targetId, validity, cancellationToken);

        logger.LogInformation("Convite {ConviteId}: {Quantidade} pessoa(s) em {Tipo} {Alvo}",
            convite.Id, concessoes.Count, targetType, targetId);

        return [.. concessoes.Select(c => new InviteResult(c.Endereco.Value, c.Concessao.Id, sendEmail, convite.Id))];
    }

    /// <summary>Concede acesso a todos os endereços de um domínio, num convite próprio.</summary>
    public async Task<AccessGrant> GrantToDomainAsync(
        string domain,
        GrantTargetType targetType,
        Guid? targetId,
        GrantValidity validity,
        Guid adminId,
        string? note = null,
        CancellationToken cancellationToken = default) =>
        (await GrantToDomainsAsync([domain], targetType, targetId, validity, adminId, note, cancellationToken))[0];

    /// <summary>
    /// Libera um ou mais domínios inteiros, com a mesma validade, num convite novo. Como no
    /// convite de pessoas, nada que já exista é alterado.
    /// </summary>
    public async Task<IReadOnlyList<AccessGrant>> GrantToDomainsAsync(
        IEnumerable<string> domains,
        GrantTargetType targetType,
        Guid? targetId,
        GrantValidity validity,
        Guid adminId,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domains);

        var dominios = domains
            .Select(d => (d ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant())
            .Where(d => d.Length > 0)
            .Distinct()
            .ToList();

        if (dominios.Count == 0)
            throw new InvalidOperationException("No domain was given.");

        if (dominios.FirstOrDefault(d => !DominioValido(d)) is { } invalido)
            throw new InvalidOperationException(LocalText.Format("{0} is not a valid domain.", invalido));

        if (dominios.FirstOrDefault(PublicEmailProviders.IsPublic) is { } publico)
            throw new InvalidOperationException(LocalText.Format(
                "{0} is a public email provider: granting it would let anyone with an account there watch. Invite those people by their own address.",
                publico));

        var convite = Invitation.Create(InvitationKind.Domains, targetType, targetId, adminId, clock.GetUtcNow(),
            validity.ExpiresAt, validity.DurationAfterFirstUse, note: note);
        var concessoes = dominios.Select(d => AccessGrant.ForInvitation(convite, GrantSubjectType.Domain, d)).ToList();

        db.Invitations.Add(convite);
        db.AccessGrants.AddRange(concessoes);
        await db.SaveChangesAsync(cancellationToken);

        return concessoes;
    }

    /// <summary>Revoga a concessão e a devolve, para o registro dizer de quem e sobre o quê.</summary>
    public async Task<AccessGrant> RevokeAsync(Guid grantId, CancellationToken cancellationToken = default)
    {
        var concessao = await db.AccessGrants.FirstOrDefaultAsync(g => g.Id == grantId, cancellationToken)
            ?? throw new InvalidOperationException("Grant not found.");

        concessao.Revoke(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Concessão {GrantId} revogada", grantId);

        return concessao;
    }

    public async Task<AccessGrant> RestoreAsync(Guid grantId, CancellationToken cancellationToken = default)
    {
        var concessao = await db.AccessGrants.FirstOrDefaultAsync(g => g.Id == grantId, cancellationToken)
            ?? throw new InvalidOperationException("Grant not found.");

        concessao.Restore();
        await db.SaveChangesAsync(cancellationToken);

        return concessao;
    }

    /// <summary>Domínio como "empresa.com.br": rótulos de letras, números e hífen, separados por ponto.</summary>
    private static bool DominioValido(string dominio) =>
        dominio.Length <= 253
        && System.Text.RegularExpressions.Regex.IsMatch(dominio, @"^(?!-)[a-z0-9-]{1,63}(?<!-)(\.(?!-)[a-z0-9-]{1,63}(?<!-))+$");
}
