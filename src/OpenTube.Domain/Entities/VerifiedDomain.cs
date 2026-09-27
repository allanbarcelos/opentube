// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Text.RegularExpressions;

namespace OpenTube.Domain.Entities;

/// <summary>
/// Um domínio de email cadastrado para acesso coletivo. A verificação por DNS existe para
/// impedir que alguém cadastre um domínio que não controla — sem ela, cadastrar
/// <c>gmail.com</c> abriria o acervo para meio mundo.
/// </summary>
public partial class VerifiedDomain
{
    /// <summary>Nome do registro TXT consultado durante a verificação.</summary>
    public const string VerificationPrefix = "_opentube-verify";

    /// <summary>Prefixo do valor esperado no registro TXT.</summary>
    public const string VerificationValuePrefix = "opentube-verify=";

    private VerifiedDomain() { }

    public Guid Id { get; private set; }

    /// <summary>Domínio em minúsculas, sem ponto final.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Valor que precisa aparecer no registro TXT.</summary>
    public string VerificationToken { get; private set; } = string.Empty;

    public DateTimeOffset? VerifiedAt { get; private set; }

    /// <summary>
    /// Endereço da porta de entrada. Por padrão é o próprio domínio; pode ser trocado por um
    /// valor não adivinhável quando o administrador não quiser expor que ele existe.
    /// </summary>
    public string EntrySlug { get; private set; } = string.Empty;

    /// <summary>Se a porta de entrada dedicada está no ar.</summary>
    public bool EntryEnabled { get; private set; } = true;

    /// <summary>
    /// Lista opcional de endereços permitidos dentro do domínio. Vazia significa que qualquer
    /// email do domínio serve.
    /// </summary>
    public string[] AllowedEmails { get; private set; } = [];

    /// <summary>Responsável pelo domínio, que recebe o link da porta de entrada.</summary>
    public string? ContactEmail { get; private set; }

    public string? Note { get; private set; }

    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? DisabledAt { get; private set; }

    public bool IsVerified => VerifiedAt is not null;

    public bool IsActive => DisabledAt is null;

    /// <summary>A porta de entrada só atende quando o domínio está verificado e ativo.</summary>
    public bool EntryAvailable => IsVerified && IsActive && EntryEnabled;

    /// <summary>Nome completo do registro TXT a consultar.</summary>
    public string VerificationRecordName => $"{VerificationPrefix}.{Name}";

    /// <summary>Valor completo esperado no registro TXT.</summary>
    public string ExpectedRecordValue => VerificationValuePrefix + VerificationToken;

    public static VerifiedDomain Register(string name, string verificationToken, Guid createdBy, DateTimeOffset now, string? contactEmail = null, string? note = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationToken);

        var normalizado = Normalize(name);

        if (!DomainPattern().IsMatch(normalizado))
            throw new ArgumentException("This is not a valid domain.", nameof(name));

        return new VerifiedDomain
        {
            Id = Guid.CreateVersion7(),
            Name = normalizado,
            VerificationToken = verificationToken.Trim(),
            EntrySlug = normalizado,
            CreatedBy = createdBy,
            CreatedAt = now,
            ContactEmail = string.IsNullOrWhiteSpace(contactEmail) ? null : contactEmail.Trim().ToLowerInvariant(),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
        };
    }

    /// <summary>Normaliza um domínio para comparação: minúsculas, sem espaços e sem ponto final.</summary>
    public static string Normalize(string? name) =>
        (name ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>
    /// Confere os registros TXT encontrados. Aceita o valor com ou sem o prefixo, porque
    /// alguns painéis de DNS reescrevem o conteúdo, e ignora aspas deixadas pelo provedor.
    /// </summary>
    public bool Matches(IEnumerable<string> txtRecords)
    {
        ArgumentNullException.ThrowIfNull(txtRecords);

        foreach (var registro in txtRecords)
        {
            var valor = (registro ?? string.Empty).Trim().Trim('"').Trim();

            if (valor.Length == 0)
                continue;

            if (string.Equals(valor, ExpectedRecordValue, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(valor, VerificationToken, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public void MarkVerified(DateTimeOffset now) => VerifiedAt ??= now;

    /// <summary>Descarta a verificação e emite um token novo, obrigando a refazer o processo.</summary>
    public void ResetVerification(string newToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newToken);

        VerificationToken = newToken.Trim();
        VerifiedAt = null;
    }

    /// <summary>Troca o endereço da porta de entrada.</summary>
    public void ChangeEntrySlug(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        EntrySlug = slug.Trim().ToLowerInvariant();
    }

    public void SetEntryEnabled(bool enabled) => EntryEnabled = enabled;

    /// <summary>
    /// Restringe o acesso a endereços específicos dentro do domínio. Lista vazia devolve o
    /// comportamento padrão, em que qualquer email do domínio serve.
    /// </summary>
    public void SetAllowedEmails(IEnumerable<string>? emails)
    {
        AllowedEmails = emails is null
            ? []
            : [.. emails
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Verifica se um endereço pode entrar por esta porta.</summary>
    public bool Accepts(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        var normalizado = email.Trim().ToLowerInvariant();
        var arroba = normalizado.IndexOf('@');

        if (arroba < 0 || !string.Equals(normalizado[(arroba + 1)..], Name, StringComparison.Ordinal))
            return false;

        return AllowedEmails.Length == 0 || AllowedEmails.Contains(normalizado, StringComparer.Ordinal);
    }

    public void SetContact(string? email) =>
        ContactEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    public void SetNote(string? note) => Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    public void Disable(DateTimeOffset now) => DisabledAt ??= now;

    public void Enable() => DisabledAt = null;

    [GeneratedRegex(@"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,}$")]
    private static partial Regex DomainPattern();
}
