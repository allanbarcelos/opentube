using System.Text.RegularExpressions;

namespace OpenTube.Domain.ValueObjects;

/// <summary>
/// Endereço de email normalizado. A comparação é sempre feita em minúsculas porque o sistema
/// identifica pessoas por email e a caixa alta não pode criar duas identidades diferentes.
/// </summary>
public readonly partial record struct EmailAddress
{
    public string Value { get; }

    private EmailAddress(string value) => Value = value;

    /// <summary>Parte do domínio, já normalizada (o que vem depois do arroba).</summary>
    public string Domain => Value[(Value.IndexOf('@') + 1)..];

    /// <summary>Parte local, já normalizada (o que vem antes do arroba).</summary>
    public string LocalPart => Value[..Value.IndexOf('@')];

    public static bool TryParse(string? input, out EmailAddress email)
    {
        email = default;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var normalized = input.Trim().ToLowerInvariant();
        if (normalized.Length > 254 || !Pattern().IsMatch(normalized))
            return false;

        email = new EmailAddress(normalized);
        return true;
    }

    public static EmailAddress Parse(string input) =>
        TryParse(input, out var email)
            ? email
            : throw new FormatException($"Endereço de email inválido: '{input}'.");

    /// <summary>Verifica se o email pertence ao domínio informado, ignorando caixa e espaços.</summary>
    public bool BelongsTo(string domain) =>
        !string.IsNullOrWhiteSpace(domain) &&
        string.Equals(Domain, domain.Trim().ToLowerInvariant(), StringComparison.Ordinal);

    public override string ToString() => Value;

    public static implicit operator string(EmailAddress email) => email.Value;

    [GeneratedRegex(@"^[a-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[a-z0-9!#$%&'*+/=?^_`{|}~-]+)*@(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,}$")]
    private static partial Regex Pattern();
}
