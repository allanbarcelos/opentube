// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Globalization;
using System.Text;

namespace OpenTube.Domain.ValueObjects;

/// <summary>
/// Identificador legível usado nas URLs de vídeos e coleções. Remove acentos para que
/// "Reunião Trimestral" e "Reuniao Trimestral" não gerem endereços diferentes.
/// </summary>
public static class Slug
{
    public const int MaxLength = 80;

    public static string From(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var lastWasHyphen = false;

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen && builder.Length > 0)
            {
                builder.Append('-');
                lastWasHyphen = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > MaxLength)
            slug = slug[..MaxLength].Trim('-');

        return slug;
    }

    /// <summary>
    /// Gera um slug garantidamente único, acrescentando sufixo numérico enquanto o candidato
    /// já existir. Cai para um sufixo aleatório quando o título é composto só de símbolos.
    /// </summary>
    public static string Unique(string? text, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);

        var baseSlug = From(text);
        if (baseSlug.Length == 0)
            baseSlug = "video";

        if (!exists(baseSlug))
            return baseSlug;

        for (var suffix = 2; suffix <= 999; suffix++)
        {
            var candidate = Truncate(baseSlug, MaxLength - 4) + "-" + suffix;
            if (!exists(candidate))
                return candidate;
        }

        return Truncate(baseSlug, MaxLength - 9) + "-" + Guid.NewGuid().ToString("n")[..8];
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length].Trim('-');
}
