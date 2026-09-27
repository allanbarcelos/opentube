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
public sealed record InviteResult(string Email, Guid GrantId, bool EmailSent);

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
    /// Concede acesso a uma lista de endereços e envia o convite. Endereço repetido sobre o
    /// mesmo alvo não gera concessão nova: reenviar o convite basta.
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
        var agora = clock.GetUtcNow();
        var resultados = new List<InviteResult>(enderecos.Count);

        foreach (var endereco in enderecos)
        {
            var concessao = await ExistenteAsync(GrantSubjectType.User, endereco.Value, targetType, targetId, cancellationToken)
                ?? AccessGrant.ForUser(endereco, targetType, targetId, adminId, agora,
                    validity.ExpiresAt, validity.DurationAfterFirstUse, note);

            if (db.Entry(concessao).State is EntityState.Detached)
                db.AccessGrants.Add(concessao);
            else
                concessao.Reschedule(concessao.StartsAt, validity.ExpiresAt, validity.DurationAfterFirstUse);

            concessao.Restore();
            await db.SaveChangesAsync(cancellationToken);

            var enviado = false;

            if (sendEmail)
            {
                var acesso = await auth.IssueInviteAsync(endereco, concessao.Id, cancellationToken);

                await email.SendAsync(EmailTemplates.Invite(
                    endereco.Value, acesso.Code, acesso.Link, rotulo, validity.Describe(), acesso.Validity),
                    cancellationToken);

                enviado = true;
            }

            resultados.Add(new InviteResult(endereco.Value, concessao.Id, enviado));
        }

        logger.LogInformation("{Quantidade} convites processados para {Alvo}", resultados.Count, rotulo);

        return resultados;
    }

    /// <summary>Concede acesso a todos os endereços de um domínio.</summary>
    public async Task<AccessGrant> GrantToDomainAsync(
        string domain,
        GrantTargetType targetType,
        Guid? targetId,
        GrantValidity validity,
        Guid adminId,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        var normalizado = domain.Trim().ToLowerInvariant();

        var concessao = await ExistenteAsync(GrantSubjectType.Domain, normalizado, targetType, targetId, cancellationToken);

        if (concessao is null)
        {
            concessao = AccessGrant.ForDomain(normalizado, targetType, targetId, adminId, clock.GetUtcNow(),
                validity.ExpiresAt, validity.DurationAfterFirstUse, note);

            db.AccessGrants.Add(concessao);
        }
        else
        {
            concessao.Reschedule(concessao.StartsAt, validity.ExpiresAt, validity.DurationAfterFirstUse);
            concessao.Restore();
        }

        await db.SaveChangesAsync(cancellationToken);

        return concessao;
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

        var concessao = AccessGrant.ForLink(
            TokenHasher.Hash(token, _options.TokenPepper),
            targetType, targetId, adminId, clock.GetUtcNow(),
            validity.ExpiresAt, maxViews, note);

        if (validity.DurationAfterFirstUse is not null)
            concessao.Reschedule(null, validity.ExpiresAt, validity.DurationAfterFirstUse);

        db.AccessGrants.Add(concessao);
        await db.SaveChangesAsync(cancellationToken);

        return new ShareLink(concessao.Id, $"{_options.PublicUrl.TrimEnd('/')}/link/{token}");
    }

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

    private Task<AccessGrant?> ExistenteAsync(
        GrantSubjectType tipo, string valor, GrantTargetType alvo, Guid? alvoId, CancellationToken cancellationToken) =>
        db.AccessGrants.FirstOrDefaultAsync(
            g => g.SubjectType == tipo && g.SubjectValue == valor && g.TargetType == alvo && g.TargetId == alvoId,
            cancellationToken);

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
