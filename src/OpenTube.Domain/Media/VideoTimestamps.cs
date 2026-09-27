// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenTube.Domain.Media;

/// <summary>Pedaço de um texto: texto comum, ou um instante do vídeo citado nele.</summary>
/// <param name="Text">O que estava escrito.</param>
/// <param name="Seconds">O instante, em segundos, quando o pedaço é um tempo do vídeo.</param>
public sealed record TextSegment(string Text, int? Seconds)
{
    public bool IsTimestamp => Seconds is not null;
}

/// <summary>
/// Tempos do vídeo escritos como as pessoas escrevem: <c>1:05:10</c>, <c>05:10</c>, <c>5:10</c>.
/// Usado para transformar o tempo citado numa mensagem em link, como no YouTube, e para ler
/// o início de cada capítulo do sumário.
/// </summary>
public static partial class VideoTimestamps
{
    /// <summary>Lê um tempo isolado. Aceita também só segundos (<c>90</c>).</summary>
    public static bool TryParse(string? text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        var valor = text?.Trim();

        if (string.IsNullOrEmpty(valor))
            return false;

        if (int.TryParse(valor, NumberStyles.None, CultureInfo.InvariantCulture, out var segundos))
        {
            time = TimeSpan.FromSeconds(segundos);
            return true;
        }

        var achado = Tempo().Match(valor);
        if (!achado.Success || achado.Length != valor.Length)
            return false;

        time = TimeSpan.FromSeconds(Segundos(achado));
        return true;
    }

    /// <summary>Escreve como se lê: <c>1:05:10</c> com horas, <c>5:10</c> sem.</summary>
    public static string Format(TimeSpan time)
    {
        var total = (int)Math.Max(0, Math.Floor(time.TotalSeconds));
        var horas = total / 3600;
        var minutos = total % 3600 / 60;
        var segundos = total % 60;

        return horas > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{horas}:{minutos:D2}:{segundos:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutos}:{segundos:D2}");
    }

    /// <summary>
    /// Separa o texto nos tempos citados. Só vira instante o que cabe no vídeo: "às 14:30" num
    /// vídeo de 10 minutos continua sendo texto.
    /// </summary>
    /// <param name="text">Texto da mensagem.</param>
    /// <param name="videoLength">Duração do vídeo; sem ela, nenhum tempo vira instante.</param>
    public static IReadOnlyList<TextSegment> Split(string? text, TimeSpan? videoLength)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        if (videoLength is not { } limite || limite <= TimeSpan.Zero)
            return [new TextSegment(text, null)];

        var partes = new List<TextSegment>();
        var posicao = 0;

        foreach (Match achado in NoTexto().Matches(text))
        {
            var segundos = Segundos(achado);
            if (segundos > limite.TotalSeconds)
                continue;

            if (achado.Index > posicao)
                partes.Add(new TextSegment(text[posicao..achado.Index], null));

            partes.Add(new TextSegment(achado.Value, segundos));
            posicao = achado.Index + achado.Length;
        }

        if (posicao < text.Length)
            partes.Add(new TextSegment(text[posicao..], null));

        return partes;
    }

    private static int Segundos(Match achado)
    {
        int Grupo(string nome) => achado.Groups[nome].Success
            ? int.Parse(achado.Groups[nome].Value, CultureInfo.InvariantCulture)
            : 0;

        return achado.Groups["h"].Success
            ? Grupo("h") * 3600 + Grupo("hm") * 60 + Grupo("hs")
            : Grupo("m") * 60 + Grupo("s");
    }

    // H:MM:SS, ou M:SS (minutos sem limite de dois dígitos, como "75:30").
    private const string Padrao = @"(?:(?<h>\d{1,2}):(?<hm>[0-5]\d):(?<hs>[0-5]\d)|(?<m>\d{1,3}):(?<s>[0-5]\d))";

    [GeneratedRegex(Padrao)]
    private static partial Regex Tempo();

    // No meio do texto: não pode estar colado a letra, número ou dois-pontos ("10:30:45:12",
    // "v1:2:30" e "a1:30" não são tempos).
    [GeneratedRegex(@"(?<![\p{L}\d:])" + Padrao + @"(?![\p{L}\d:])")]
    private static partial Regex NoTexto();
}
