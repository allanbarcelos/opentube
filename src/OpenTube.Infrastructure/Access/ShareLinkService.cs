// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.Extensions.Options;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Security;

namespace OpenTube.Infrastructure.Access;

/// <summary>Link de compartilhamento recém-criado.</summary>
/// <param name="GrantId">Concessão correspondente.</param>
/// <param name="Url">Endereço completo, exibido uma única vez.</param>
public sealed record ShareLink(Guid GrantId, string Url);

/// <summary>
/// Links secretos de compartilhamento: criar um e mostrar o endereço dele à administração. O
/// token só existe no endereço; o banco guarda o resumo, para conferir, e o token cifrado, para
/// a administração poder copiá-lo de novo.
/// </summary>
public class ShareLinkService(OpenTubeDbContext db, IOptions<SecurityOptions> options, TimeProvider clock)
{
    private readonly SecurityOptions _options = options.Value;

    /// <summary>Cria um link secreto, num convite próprio, e devolve o endereço completo.</summary>
    public async Task<ShareLink> CreateShareLinkAsync(
        GrantTargetType targetType,
        Guid? targetId,
        GrantValidity validity,
        Guid adminId,
        int? maxViews = null,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        var token = OneTimeCode.GenerateToken();

        var convite = Invitation.Create(InvitationKind.Link, targetType, targetId, adminId, clock.GetUtcNow(),
            validity.ExpiresAt, validity.DurationAfterFirstUse, maxViews, note);
        var concessao = AccessGrant.ForInvitation(convite, GrantSubjectType.Link, TokenHasher.Hash(token, _options.TokenPepper));
        concessao.KeepSealedToken(LinkSealer.Seal(token, _options.TokenPepper));

        db.Invitations.Add(convite);
        db.AccessGrants.Add(concessao);
        await db.SaveChangesAsync(cancellationToken);

        return new ShareLink(concessao.Id, EnderecoDoLink(token));
    }

    /// <summary>
    /// Endereço de um link secreto, para a administração ver e copiar. Nulo para os links
    /// criados antes de o token ser guardado cifrado: deles só existe o resumo.
    /// </summary>
    public string? ShareLinkAddress(AccessGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);

        return grant.SubjectType is GrantSubjectType.Link && LinkSealer.Open(grant.SealedToken, _options.TokenPepper) is { } token
            ? EnderecoDoLink(token)
            : null;
    }

    private string EnderecoDoLink(string token) => $"{_options.PublicUrl.TrimEnd('/')}/link/{token}";
}
