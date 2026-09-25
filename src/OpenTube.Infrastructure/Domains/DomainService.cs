using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Email;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Security;

namespace OpenTube.Infrastructure.Domains;

/// <summary>Resultado de uma tentativa de verificação por DNS.</summary>
/// <param name="Verified">Se o registro foi encontrado.</param>
/// <param name="FoundRecords">O que havia no DNS, para o administrador comparar.</param>
public sealed record DomainVerification(bool Verified, IReadOnlyList<string> FoundRecords);

/// <summary>Por que um pedido de entrada pela porta do domínio não foi adiante.</summary>
public enum DomainEntryFailure
{
    None = 0,
    DomainNotFound = 1,
    EmailNotAccepted = 2,
    RateLimited = 3,
    InvalidEmail = 4
}

/// <summary>
/// Cadastro e verificação de domínios. A comprovação por DNS é o que impede alguém de
/// cadastrar um domínio alheio e abrir o acervo para os usuários dele.
/// </summary>
public class DomainService(
    OpenTubeDbContext db,
    IDnsTxtLookup dns,
    PasswordlessAuthService auth,
    IEmailSender email,
    IOptions<SecurityOptions> options,
    TimeProvider clock,
    ILogger<DomainService> logger)
{
    private readonly SecurityOptions _options = options.Value;

    public async Task<VerifiedDomain> RegisterAsync(
        string name,
        Guid adminId,
        string? contactEmail = null,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        var normalizado = VerifiedDomain.Normalize(name);

        if (await db.VerifiedDomains.AnyAsync(d => d.Name == normalizado, cancellationToken))
            throw new InvalidOperationException($"O domínio '{normalizado}' já está cadastrado.");

        var dominio = VerifiedDomain.Register(
            normalizado, OneTimeCode.GenerateToken(16), adminId, clock.GetUtcNow(), contactEmail, note);

        db.VerifiedDomains.Add(dominio);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Domínio {Dominio} cadastrado, aguardando verificação", normalizado);

        return dominio;
    }

    /// <summary>Consulta o DNS e marca o domínio como verificado se o registro conferir.</summary>
    public async Task<DomainVerification> VerifyAsync(Guid domainId, CancellationToken cancellationToken = default)
    {
        var dominio = await CarregarAsync(domainId, cancellationToken);

        var registros = await dns.GetTxtAsync(dominio.VerificationRecordName, cancellationToken);

        if (!dominio.Matches(registros))
        {
            logger.LogInformation("Verificação de {Dominio} não encontrou o registro esperado", dominio.Name);
            return new DomainVerification(false, registros);
        }

        dominio.MarkVerified(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Domínio {Dominio} verificado", dominio.Name);

        return new DomainVerification(true, registros);
    }

    /// <summary>Emite um token novo, obrigando a refazer a publicação do registro.</summary>
    public async Task<VerifiedDomain> ResetVerificationAsync(Guid domainId, CancellationToken cancellationToken = default)
    {
        var dominio = await CarregarAsync(domainId, cancellationToken);

        dominio.ResetVerification(OneTimeCode.GenerateToken(16));
        await db.SaveChangesAsync(cancellationToken);

        return dominio;
    }

    public async Task<VerifiedDomain> UpdateAsync(
        Guid domainId,
        string? entrySlug,
        bool entryEnabled,
        IEnumerable<string>? allowedEmails,
        string? contactEmail,
        string? note,
        CancellationToken cancellationToken = default)
    {
        var dominio = await CarregarAsync(domainId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(entrySlug))
        {
            var normalizado = entrySlug.Trim().ToLowerInvariant();

            if (await db.VerifiedDomains.AnyAsync(d => d.EntrySlug == normalizado && d.Id != domainId, cancellationToken))
                throw new InvalidOperationException("Já existe outra porta de entrada com este endereço.");

            dominio.ChangeEntrySlug(normalizado);
        }

        dominio.SetEntryEnabled(entryEnabled);
        dominio.SetAllowedEmails(allowedEmails);
        dominio.SetContact(contactEmail);
        dominio.SetNote(note);

        await db.SaveChangesAsync(cancellationToken);

        return dominio;
    }

    public Task<VerifiedDomain?> FindAsync(Guid domainId, CancellationToken cancellationToken = default) =>
        db.VerifiedDomains.FirstOrDefaultAsync(d => d.Id == domainId, cancellationToken);

    /// <summary>Encontra o domínio pela porta de entrada, já conferindo se ela está no ar.</summary>
    public async Task<VerifiedDomain?> FindOpenEntryAsync(string entrySlug, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entrySlug))
            return null;

        var normalizado = entrySlug.Trim().ToLowerInvariant();

        var dominio = await db.VerifiedDomains
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.EntrySlug == normalizado, cancellationToken);

        return dominio?.EntryAvailable == true ? dominio : null;
    }

    public Task<List<VerifiedDomain>> ListAsync(CancellationToken cancellationToken = default) =>
        db.VerifiedDomains.AsNoTracking().OrderBy(d => d.Name).ToListAsync(cancellationToken);

    /// <summary>Endereço completo da porta de entrada, para copiar e repassar.</summary>
    public string EntryUrl(VerifiedDomain dominio) =>
        $"{_options.PublicUrl.TrimEnd('/')}/d/{dominio.EntrySlug}";

    /// <summary>
    /// Envia ao responsável pelo domínio o endereço da porta de entrada, que é o que ele
    /// repassa às pessoas da organização.
    /// </summary>
    public async Task<bool> SendEntryLinkAsync(Guid domainId, CancellationToken cancellationToken = default)
    {
        var dominio = await CarregarAsync(domainId, cancellationToken);

        if (dominio.ContactEmail is null || !dominio.EntryAvailable)
            return false;

        await email.SendAsync(EmailTemplates.DomainEntry(
            dominio.ContactEmail, dominio.Name, EntryUrl(dominio)), cancellationToken);

        return true;
    }

    /// <summary>
    /// Pedido de código pela porta do domínio. O endereço precisa pertencer ao domínio e, se
    /// houver lista de permitidos, estar nela.
    /// </summary>
    public async Task<DomainEntryFailure> RequestEntryCodeAsync(
        string entrySlug,
        string emailInput,
        string? ip = null,
        CancellationToken cancellationToken = default)
    {
        var dominio = await FindOpenEntryAsync(entrySlug, cancellationToken);

        if (dominio is null)
            return DomainEntryFailure.DomainNotFound;

        if (!EmailAddress.TryParse(emailInput, out var endereco))
            return DomainEntryFailure.InvalidEmail;

        if (!dominio.Accepts(endereco.Value))
            return DomainEntryFailure.EmailNotAccepted;

        var resultado = await auth.RequestCodeAsync(
            endereco.Value, AuthPurpose.DomainEntry, ip, cancellationToken: cancellationToken);

        return resultado.Failure is AuthFailure.RateLimited
            ? DomainEntryFailure.RateLimited
            : DomainEntryFailure.None;
    }

    private async Task<VerifiedDomain> CarregarAsync(Guid domainId, CancellationToken cancellationToken) =>
        await db.VerifiedDomains.FirstOrDefaultAsync(d => d.Id == domainId, cancellationToken)
        ?? throw new InvalidOperationException("Domínio não encontrado.");
}
