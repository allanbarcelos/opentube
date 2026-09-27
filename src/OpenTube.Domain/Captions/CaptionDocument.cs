// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenTube.Domain.Captions;

/// <summary>Um trecho de legenda: quando entra, quando sai e o que diz.</summary>
/// <param name="Start">Entrada na tela.</param>
/// <param name="End">Saída da tela.</param>
/// <param name="Text">Texto, podendo ter mais de uma linha e marcação simples (&lt;i&gt;, &lt;b&gt;).</param>
/// <param name="Settings">Ajustes de posição do WebVTT (<c>align:start</c> e afins), preservados.</param>
public sealed record CaptionCue(TimeSpan Start, TimeSpan End, string Text, string? Settings = null);

/// <summary>
/// Problema num arquivo ou numa edição de legenda. A mensagem é a chave em inglês, traduzida
/// na borda; os argumentos (a linha, o trecho) ficam separados para a tradução.
/// </summary>
public sealed class CaptionFormatException(string key, params object[] args)
    : FormatException(string.Format(CultureInfo.InvariantCulture, key, args))
{
    public string Key { get; } = key;

    public IReadOnlyList<object> Args { get; } = args;
}

/// <summary>
/// Uma legenda inteira. Lê WebVTT e também SRT — o formato que a maioria das ferramentas de
/// legendagem exporta —, e sempre escreve WebVTT, o que o navegador entende. É o que alimenta
/// o editor e o que confere tudo antes de ser guardado.
/// </summary>
public sealed partial class CaptionDocument
{
    /// <summary>Mais trechos que isso é engano ou abuso: um filme longo tem uns dois mil.</summary>
    public const int MaxCues = 20000;

    /// <summary>Texto de um trecho. Legenda é para ser lida em poucos segundos.</summary>
    public const int MaxCueLength = 1000;

    private CaptionDocument(IReadOnlyList<CaptionCue> cues) => Cues = cues;

    public IReadOnlyList<CaptionCue> Cues { get; }

    /// <summary>Só a fala, sem tempos nem marcação: é o que entra no índice de busca.</summary>
    public string PlainText => string.Join(' ', Cues
        .Select(c => Marcacao().Replace(c.Text, string.Empty).Replace('\n', ' ').Trim())
        .Where(t => t.Length > 0));

    /// <summary>
    /// Monta a legenda a partir de trechos já separados, como vêm do editor. Confere cada um
    /// e ordena pela entrada, que é a ordem que o WebVTT exige.
    /// </summary>
    public static CaptionDocument FromCues(IEnumerable<CaptionCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);

        var lista = new List<CaptionCue>();
        var numero = 0;

        foreach (var trecho in cues)
        {
            numero++;
            lista.Add(Validar(trecho, numero));

            if (lista.Count > MaxCues)
                throw new CaptionFormatException("A caption can have at most {0} cues.", MaxCues);
        }

        return new CaptionDocument([.. lista.OrderBy(c => c.Start).ThenBy(c => c.End)]);
    }

    /// <summary>Lê um arquivo WebVTT ou SRT.</summary>
    public static CaptionDocument Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new CaptionFormatException("The caption file is empty.");

        var linhas = content.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var primeira = Array.FindIndex(linhas, l => l.Trim().Length > 0);

        if (linhas[primeira].TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal))
            return LerWebVtt(linhas, primeira);

        if (PareceSrt(linhas, primeira))
            return LerSrt(linhas, primeira);

        throw new CaptionFormatException("The file must be WebVTT (starting with WEBVTT) or SRT.");
    }

    public string ToWebVtt()
    {
        var saida = new StringBuilder("WEBVTT\n");

        foreach (var trecho in Cues)
        {
            saida.Append('\n')
                .Append(FormatTime(trecho.Start)).Append(" --> ").Append(FormatTime(trecho.End));

            if (!string.IsNullOrWhiteSpace(trecho.Settings))
                saida.Append(' ').Append(trecho.Settings);

            saida.Append('\n').Append(trecho.Text).Append('\n');
        }

        return saida.ToString();
    }

    /// <summary>Tempo no formato do WebVTT, sempre com horas: <c>00:01:02.500</c>.</summary>
    public static string FormatTime(TimeSpan tempo) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{(int)tempo.TotalHours:D2}:{tempo.Minutes:D2}:{tempo.Seconds:D2}.{tempo.Milliseconds:D3}");

    /// <summary>
    /// Lê um tempo com ou sem horas (<c>01:02.500</c> ou <c>00:01:02.500</c>), com ponto ou
    /// vírgula nos milissegundos — a vírgula é a do SRT.
    /// </summary>
    public static bool TryParseTime(string? texto, out TimeSpan tempo)
    {
        tempo = TimeSpan.Zero;

        if (string.IsNullOrWhiteSpace(texto))
            return false;

        var achado = Tempo().Match(texto.Trim());
        if (!achado.Success)
            return false;

        var horas = achado.Groups["h"].Success ? int.Parse(achado.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        var minutos = int.Parse(achado.Groups["m"].Value, CultureInfo.InvariantCulture);
        var segundos = int.Parse(achado.Groups["s"].Value, CultureInfo.InvariantCulture);
        var milissegundos = int.Parse(achado.Groups["ms"].Value.PadRight(3, '0'), CultureInfo.InvariantCulture);

        if (minutos > 59 || segundos > 59)
            return false;

        tempo = new TimeSpan(0, horas, minutos, segundos, milissegundos);
        return true;
    }

    private static CaptionCue Validar(CaptionCue trecho, int numero)
    {
        if (trecho.Start < TimeSpan.Zero)
            throw new CaptionFormatException("Cue {0}: the start time cannot be negative.", numero);

        if (trecho.End <= trecho.Start)
            throw new CaptionFormatException("Cue {0}: the end time must come after the start time.", numero);

        // Linha vazia encerra o trecho no WebVTT, e "-->" seria lido como um novo tempo.
        var texto = string.Join('\n', (trecho.Text ?? string.Empty)
            .Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0));

        if (texto.Length == 0)
            throw new CaptionFormatException("Cue {0}: the text is empty.", numero);

        if (texto.Contains("-->", StringComparison.Ordinal))
            throw new CaptionFormatException("Cue {0}: the text cannot contain \"-->\".", numero);

        if (texto.Length > MaxCueLength)
            throw new CaptionFormatException("Cue {0}: the text is longer than {1} characters.", numero, MaxCueLength);

        var ajustes = string.IsNullOrWhiteSpace(trecho.Settings) ? null : trecho.Settings.Trim();

        return trecho with { Text = texto, Settings = ajustes };
    }

    private static CaptionDocument LerWebVtt(string[] linhas, int primeira)
    {
        var trechos = new List<CaptionCue>();
        var i = primeira + 1;

        // O cabeçalho vai até a primeira linha em branco.
        while (i < linhas.Length && linhas[i].Trim().Length > 0)
            i++;

        while (i < linhas.Length)
        {
            if (linhas[i].Trim().Length == 0)
            {
                i++;
                continue;
            }

            var inicioDoBloco = i;
            var bloco = new List<string>();

            while (i < linhas.Length && linhas[i].Trim().Length > 0)
                bloco.Add(linhas[i++]);

            var cabeca = bloco[0].Trim();

            // Comentários e estilos não são trechos de legenda.
            if (cabeca.StartsWith("NOTE", StringComparison.Ordinal)
                || cabeca.StartsWith("STYLE", StringComparison.Ordinal)
                || cabeca.StartsWith("REGION", StringComparison.Ordinal))
                continue;

            // Primeira linha pode ser um identificador; o tempo vem nela ou na seguinte.
            var linhaDoTempo = bloco[0].Contains("-->", StringComparison.Ordinal) ? 0 : 1;

            if (linhaDoTempo >= bloco.Count || !bloco[linhaDoTempo].Contains("-->", StringComparison.Ordinal))
                throw new CaptionFormatException("Line {0}: expected a time like 00:00:01.000 --> 00:00:03.000.", inicioDoBloco + 1);

            trechos.Add(LerTrecho(bloco, linhaDoTempo, inicioDoBloco));
        }

        return FromCues(trechos);
    }

    private static bool PareceSrt(string[] linhas, int primeira) =>
        primeira + 1 < linhas.Length
        && linhas[primeira].Trim().All(char.IsAsciiDigit)
        && linhas[primeira + 1].Contains("-->", StringComparison.Ordinal);

    private static CaptionDocument LerSrt(string[] linhas, int primeira)
    {
        var trechos = new List<CaptionCue>();
        var i = primeira;

        while (i < linhas.Length)
        {
            if (linhas[i].Trim().Length == 0)
            {
                i++;
                continue;
            }

            var inicioDoBloco = i;
            var bloco = new List<string>();

            while (i < linhas.Length && linhas[i].Trim().Length > 0)
                bloco.Add(linhas[i++]);

            // Número do bloco e, na linha seguinte, o tempo.
            if (bloco.Count < 2 || !bloco[1].Contains("-->", StringComparison.Ordinal))
                throw new CaptionFormatException("Line {0}: expected a time like 00:00:01.000 --> 00:00:03.000.", inicioDoBloco + 1);

            // O SRT não tem ajustes de posição: o que vier depois dos tempos é descartado.
            var trecho = LerTrecho(bloco, 1, inicioDoBloco);
            trechos.Add(trecho with { Settings = null });
        }

        return FromCues(trechos);
    }

    private static CaptionCue LerTrecho(List<string> bloco, int linhaDoTempo, int inicioDoBloco)
    {
        var numeroDaLinha = inicioDoBloco + linhaDoTempo + 1;
        var partes = bloco[linhaDoTempo].Split("-->", 2, StringSplitOptions.TrimEntries);
        var depois = partes[1].Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!TryParseTime(partes[0], out var inicio) || depois.Length == 0 || !TryParseTime(depois[0], out var fim))
            throw new CaptionFormatException("Line {0}: expected a time like 00:00:01.000 --> 00:00:03.000.", numeroDaLinha);

        var texto = string.Join('\n', bloco.Skip(linhaDoTempo + 1));

        if (texto.Trim().Length == 0)
            throw new CaptionFormatException("Line {0}: the cue has no text.", numeroDaLinha);

        return new CaptionCue(inicio, fim, texto, depois.Length > 1 ? depois[1] : null);
    }

    [GeneratedRegex(@"^(?:(?<h>\d{1,3}):)?(?<m>\d{2}):(?<s>\d{2})[.,](?<ms>\d{1,3})$")]
    private static partial Regex Tempo();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Marcacao();
}
