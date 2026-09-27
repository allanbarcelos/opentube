// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenTube.Domain.Captions;

/// <summary>Códigos de idioma das legendas, no formato BCP 47 (<c>pt-BR</c>, <c>en</c>).</summary>
public static partial class CaptionLanguage
{
    /// <summary>
    /// Pedido de transcrição sem idioma definido: o Whisper detecta. A legenda fica com este
    /// código provisório até a transcrição dizer qual é o idioma.
    /// </summary>
    public const string Auto = "auto";

    public static bool IsAuto(string? code) => string.Equals(code?.Trim(), Auto, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Forma guardada: minúsculas, que é também a do nome do arquivo. Recusa o que não parece um
    /// código, porque ele vira parte do caminho no storage e do atributo <c>srclang</c>.
    /// </summary>
    public static string Normalize(string? code)
    {
        var normalizado = (code ?? string.Empty).Trim().Replace('_', '-').ToLowerInvariant();

        if (normalizado == Auto)
            return Auto;

        return Codigo().IsMatch(normalizado)
            ? normalizado
            : throw new ArgumentException("Use a language code like pt-BR or en.");
    }

    /// <summary>
    /// Código que a ferramenta de transcrição entende: só o idioma, sem a região. O Whisper
    /// reconhece "pt", não "pt-br".
    /// </summary>
    public static string TranscriptionCode(string code) => IsAuto(code) ? Auto : Normalize(code).Split('-')[0];

    /// <summary>Nome do idioma na própria língua ("Português (Brasil)"), para o rótulo padrão.</summary>
    public static string DisplayName(string code)
    {
        if (IsAuto(code))
            return "Automatic detection";

        try
        {
            var nome = CultureInfo.GetCultureInfo(Normalize(code)).NativeName;
            return nome.Length > 0 ? char.ToUpper(nome[0], CultureInfo.InvariantCulture) + nome[1..] : code;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    [GeneratedRegex("^[a-z]{2,3}(-[a-z0-9]{2,8}){0,2}$")]
    private static partial Regex Codigo();
}
