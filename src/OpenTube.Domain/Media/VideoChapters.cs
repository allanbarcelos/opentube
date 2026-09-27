// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Media;

/// <summary>Um capítulo do sumário: onde começa e como se chama.</summary>
/// <param name="StartSeconds">Início, em segundos.</param>
/// <param name="Title">Título.</param>
public sealed record Chapter(int StartSeconds, string Title);

/// <summary>Capítulo recusado, com a mensagem (em inglês, traduzida na tela) e os valores dela.</summary>
public sealed class ChapterException(string key, params object[] args) : Exception(string.Format(key, args))
{
    public string Key { get; } = key;

    public IReadOnlyList<object> Args { get; } = args;
}

/// <summary>
/// Sumário de um vídeo, como o do YouTube: cada capítulo vai do seu início até o início do
/// próximo (o último, até o fim do vídeo).
/// </summary>
public static class VideoChapters
{
    public const int MaxChapters = 100;

    public const int MaxTitleLength = 100;

    /// <summary>
    /// Confere e ordena o que veio do editor. Linhas totalmente vazias são ignoradas (o editor
    /// deixa uma em branco para ser preenchida); uma linha pela metade é erro.
    /// </summary>
    /// <param name="rows">Tempo e título de cada linha, como digitados.</param>
    /// <param name="videoLength">Duração do vídeo: nenhum capítulo começa depois do fim.</param>
    public static IReadOnlyList<Chapter> Parse(IEnumerable<(string? Start, string? Title)> rows, TimeSpan videoLength)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var capitulos = new List<Chapter>();
        var linha = 0;

        foreach (var (inicio, titulo) in rows)
        {
            linha++;
            var tempoTexto = inicio?.Trim() ?? string.Empty;
            var nome = titulo?.Trim() ?? string.Empty;

            if (tempoTexto.Length == 0 && nome.Length == 0)
                continue;

            if (!VideoTimestamps.TryParse(tempoTexto, out var tempo))
                throw new ChapterException("Chapter {0}: write the start like 0:00, 5:10 or 1:05:10.", linha);

            if (nome.Length == 0)
                throw new ChapterException("Chapter {0}: the title is empty.", linha);

            if (nome.Length > MaxTitleLength)
                throw new ChapterException("Chapter {0}: the title is longer than {1} characters.", linha, MaxTitleLength);

            if (videoLength > TimeSpan.Zero && tempo >= videoLength)
                throw new ChapterException("Chapter {0}: {1} is past the end of the video ({2}).",
                    linha, VideoTimestamps.Format(tempo), VideoTimestamps.Format(videoLength));

            capitulos.Add(new Chapter((int)tempo.TotalSeconds, nome));
        }

        if (capitulos.Count > MaxChapters)
            throw new ChapterException("A video can have at most {0} chapters.", MaxChapters);

        var ordenados = capitulos.OrderBy(c => c.StartSeconds).ToList();

        for (var i = 1; i < ordenados.Count; i++)
        {
            if (ordenados[i].StartSeconds == ordenados[i - 1].StartSeconds)
                throw new ChapterException("Two chapters start at {0}.", VideoTimestamps.Format(TimeSpan.FromSeconds(ordenados[i].StartSeconds)));
        }

        return ordenados;
    }

    /// <summary>Onde cada capítulo termina: no início do seguinte, ou no fim do vídeo.</summary>
    public static int EndOf(IReadOnlyList<Chapter> chapters, int index, TimeSpan videoLength)
    {
        ArgumentNullException.ThrowIfNull(chapters);

        return index + 1 < chapters.Count
            ? chapters[index + 1].StartSeconds
            : (int)Math.Ceiling(videoLength.TotalSeconds);
    }
}
