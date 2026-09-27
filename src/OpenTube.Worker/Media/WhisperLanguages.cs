namespace OpenTube.Worker.Media;

/// <summary>
/// Idiomas do Whisper. O servidor do whisper.cpp informa o idioma detectado pelo nome em inglês
/// ("portuguese"); a legenda guarda o código ("pt"). A tabela é a do próprio whisper.cpp.
/// </summary>
public static class WhisperLanguages
{
    private static readonly Dictionary<string, string> PorNome = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "en", ["chinese"] = "zh", ["german"] = "de", ["spanish"] = "es", ["russian"] = "ru",
        ["korean"] = "ko", ["french"] = "fr", ["japanese"] = "ja", ["portuguese"] = "pt", ["turkish"] = "tr",
        ["polish"] = "pl", ["catalan"] = "ca", ["dutch"] = "nl", ["arabic"] = "ar", ["swedish"] = "sv",
        ["italian"] = "it", ["indonesian"] = "id", ["hindi"] = "hi", ["finnish"] = "fi", ["vietnamese"] = "vi",
        ["hebrew"] = "he", ["ukrainian"] = "uk", ["greek"] = "el", ["malay"] = "ms", ["czech"] = "cs",
        ["romanian"] = "ro", ["danish"] = "da", ["hungarian"] = "hu", ["tamil"] = "ta", ["norwegian"] = "no",
        ["thai"] = "th", ["urdu"] = "ur", ["croatian"] = "hr", ["bulgarian"] = "bg", ["lithuanian"] = "lt",
        ["latin"] = "la", ["maori"] = "mi", ["malayalam"] = "ml", ["welsh"] = "cy", ["slovak"] = "sk",
        ["telugu"] = "te", ["persian"] = "fa", ["latvian"] = "lv", ["bengali"] = "bn", ["serbian"] = "sr",
        ["azerbaijani"] = "az", ["slovenian"] = "sl", ["kannada"] = "kn", ["estonian"] = "et", ["macedonian"] = "mk",
        ["breton"] = "br", ["basque"] = "eu", ["icelandic"] = "is", ["armenian"] = "hy", ["nepali"] = "ne",
        ["mongolian"] = "mn", ["bosnian"] = "bs", ["kazakh"] = "kk", ["albanian"] = "sq", ["swahili"] = "sw",
        ["galician"] = "gl", ["marathi"] = "mr", ["punjabi"] = "pa", ["sinhala"] = "si", ["khmer"] = "km",
        ["shona"] = "sn", ["yoruba"] = "yo", ["somali"] = "so", ["afrikaans"] = "af", ["occitan"] = "oc",
        ["georgian"] = "ka", ["belarusian"] = "be", ["tajik"] = "tg", ["sindhi"] = "sd", ["gujarati"] = "gu",
        ["amharic"] = "am", ["yiddish"] = "yi", ["lao"] = "lo", ["uzbek"] = "uz", ["faroese"] = "fo",
        ["haitian creole"] = "ht", ["pashto"] = "ps", ["turkmen"] = "tk", ["nynorsk"] = "nn", ["maltese"] = "mt",
        ["sanskrit"] = "sa", ["luxembourgish"] = "lb", ["myanmar"] = "my", ["tibetan"] = "bo", ["tagalog"] = "tl",
        ["malagasy"] = "mg", ["assamese"] = "as", ["tatar"] = "tt", ["hawaiian"] = "haw", ["lingala"] = "ln",
        ["hausa"] = "ha", ["bashkir"] = "ba", ["javanese"] = "jw", ["sundanese"] = "su", ["cantonese"] = "yue"
    };

    /// <summary>
    /// Código do idioma a partir do que o Whisper devolveu, que pode ser o nome ("portuguese")
    /// ou já o código ("pt"). Nulo quando não reconhecido.
    /// </summary>
    public static string? ToCode(string? whisperLanguage)
    {
        var valor = whisperLanguage?.Trim();

        if (string.IsNullOrEmpty(valor))
            return null;

        if (PorNome.TryGetValue(valor, out var codigo))
            return codigo;

        return PorNome.ContainsValue(valor.ToLowerInvariant()) ? valor.ToLowerInvariant() : null;
    }
}
