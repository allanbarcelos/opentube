// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Email;
using OpenTube.Infrastructure.Localization;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Security;

namespace OpenTube.Infrastructure.Access;

/// <summary>Como a validade de uma concessão foi definida pelo administrador.</summary>
/// <param name="ExpiresAt">Data fixa de término, quando houver.</param>
/// <param name="DurationAfterFirstUse">Prazo contado a partir do primeiro acesso.</param>
public readonly record struct GrantValidity(DateTimeOffset? ExpiresAt, TimeSpan? DurationAfterFirstUse)
{
    /// <summary>Acesso sem prazo.</summary>
    public static GrantValidity Forever => new(null, null);

    public static GrantValidity Until(DateTimeOffset when) => new(when, null);

    public static GrantValidity For(TimeSpan afterFirstUse) => new(null, afterFirstUse);

    /// <summary>Descrição usada no email de convite, no idioma do pedido.</summary>
    public string Describe() => this switch
    {
        { DurationAfterFirstUse: { } prazo } => LocalText.Format("{0} days from the first visit", (int)Math.Round(prazo.TotalDays)),
        { ExpiresAt: { } fim } => LocalText.Format("until {0}", fim.ToLocalTime().ToString("d")),
        _ => LocalText.Get("no end date")
    };
}

/// <summary>Resultado do convite de uma pessoa.</summary>
/// <param name="Email">Endereço convidado.</param>
/// <param name="GrantId">Concessão criada.</param>
/// <param name="EmailSent">Se o convite foi despachado.</param>
/// <param name="InvitationId">Convite de que a concessão faz parte.</param>
public sealed record InviteResult(string Email, Guid GrantId, bool EmailSent, Guid InvitationId);

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

/// <summary>Link de compartilhamento recém-criado.</summary>
/// <param name="GrantId">Concessão correspondente.</param>
/// <param name="Url">Endereço completo, exibido uma única vez.</param>
public sealed record ShareLink(Guid GrantId, string Url);

/// <summary>
/// Concede e revoga acesso. Toda liberação nomeada passa por aqui, inclusive o convite por
/// email e o link secreto.
/// </summary>
public class GrantService(
    OpenTubeDbContext db,
    PasswordlessAuthService auth,
    IEmailSender email,
    IOptions<SecurityOptions> options,
    TimeProvider clock,
    ILogger<GrantService> logger)
{
    private readonly SecurityOptions _options = options.Value;

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

        var rotulo = await DescreverAlvoAsync(targetType, targetId, cancellationToken);

        var convite = Invitation.Create(InvitationKind.People, targetType, targetId, adminId, clock.GetUtcNow(),
            validity.ExpiresAt, validity.DurationAfterFirstUse, note: note);
        var concessoes = enderecos
            .Select(e => (Endereco: e, Concessao: AccessGrant.ForInvitation(convite, GrantSubjectType.User, e.Value)))
            .ToList();

        db.Invitations.Add(convite);
        db.AccessGrants.AddRange(concessoes.Select(c => c.Concessao));
        await db.SaveChangesAsync(cancellationToken);

        var resultados = new List<InviteResult>(concessoes.Count);

        foreach (var (endereco, concessao) in concessoes)
        {
            var enviado = false;

            if (sendEmail)
            {
                var acesso = await auth.IssueInviteAsync(endereco, concessao.Id, cancellationToken);

                await email.SendAsync(EmailTemplates.Invite(
                    endereco.Value, acesso.Code, acesso.Link, rotulo, validity.Describe(), acesso.Validity),
                    cancellationToken);

                enviado = true;
            }

            resultados.Add(new InviteResult(endereco.Value, concessao.Id, enviado, convite.Id));
        }

        logger.LogInformation("Convite {ConviteId}: {Quantidade} pessoa(s) em {Alvo}", convite.Id, resultados.Count, rotulo);

        return resultados;
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

        var convite = Invitation.Create(InvitationKind.Domains, targetType, targetId, adminId, clock.GetUtcNow(),
            validity.ExpiresAt, validity.DurationAfterFirstUse, note: note);
        var concessoes = dominios.Select(d => AccessGrant.ForInvitation(convite, GrantSubjectType.Domain, d)).ToList();

        db.Invitations.Add(convite);
        db.AccessGrants.AddRange(concessoes);
        await db.SaveChangesAsync(cancellationToken);

        return concessoes;
    }

    /// <summary>
    /// Cria um link secreto de compartilhamento. O endereço é devolvido uma única vez: só o
    /// resumo do token fica guardado, então não há como exibi-lo de novo depois.
    /// </summary>
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

    /// <summary>Revoga uma concessão. O acesso cai na avaliação seguinte.</summary>
    public async Task RevokeAsync(Guid grantId, CancellationToken cancellationToken = default)
    {
        var concessao = await db.AccessGrants.FirstOrDefaultAsync(g => g.Id == grantId, cancellationToken)
            ?? throw new InvalidOperationException("Grant not found.");

        concessao.Revoke(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Concessão {GrantId} revogada", grantId);
    }

    public async Task RestoreAsync(Guid grantId, CancellationToken cancellationToken = default)
    {
        var concessao = await db.AccessGrants.FirstOrDefaultAsync(g => g.Id == grantId, cancellationToken)
            ?? throw new InvalidOperationException("Grant not found.");

        concessao.Restore();
        await db.SaveChangesAsync(cancellationToken);
    }

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

    private static InvitationKind TipoDoConvite(GrantSubjectType tipo) => tipo switch
    {
        GrantSubjectType.Domain => InvitationKind.Domains,
        GrantSubjectType.Link => InvitationKind.Link,
        _ => InvitationKind.People
    };

    /// <summary>Domínio como "empresa.com.br": rótulos de letras, números e hífen, separados por ponto.</summary>
    public static bool DominioValido(string dominio) =>
        dominio.Length <= 253
        && System.Text.RegularExpressions.Regex.IsMatch(dominio, @"^(?!-)[a-z0-9-]{1,63}(?<!-)(\.(?!-)[a-z0-9-]{1,63}(?<!-))+$");

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

    /// <summary>Concessões cujo sujeito é um domínio, usadas na página do domínio.</summary>
    public Task<List<AccessGrant>> ListForDomainAsync(string domain, CancellationToken cancellationToken = default)
    {
        var normalizado = (domain ?? string.Empty).Trim().ToLowerInvariant();

        return db.AccessGrants
            .AsNoTracking()
            .Where(g => g.SubjectType == GrantSubjectType.Domain && g.SubjectValue == normalizado)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Nome do que foi liberado, para aparecer no convite.</summary>
    private async Task<string> DescreverAlvoAsync(GrantTargetType tipo, Guid? alvoId, CancellationToken cancellationToken) => tipo switch
    {
        GrantTargetType.All => LocalText.Get("The whole library"),
        GrantTargetType.Video => await db.Videos
            .Where(v => v.Id == alvoId)
            .Select(v => v.Title)
            .FirstOrDefaultAsync(cancellationToken) ?? LocalText.Get("A video"),
        GrantTargetType.Collection => await db.Collections
            .Where(c => c.Id == alvoId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? LocalText.Get("A collection"),
        _ => LocalText.Get("content")
    };
}
