// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Text;

namespace OpenTube.Worker.Media;

/// <summary>
/// Leitura de arquivos WebVTT. Serve para extrair o texto falado que alimenta a busca, e
/// para conferir que um arquivo enviado à mão é mesmo uma legenda.
/// </summary>
public static class VttParser
{
    /// <summary>Verifica se o conteúdo é um WebVTT reconhecível.</summary>
    public static bool IsWebVtt(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        // A marca de ordem de bytes é comum em arquivo salvo no Windows.
        var texto = content.TrimStart('﻿').TrimStart();

        return texto.StartsWith("WEBVTT", StringComparison.Ordinal);
    }

    /// <summary>
    /// Extrai apenas a fala, descartando tempos, numeração e marcação. É esse texto que entra
    /// no índice de busca: procurar por uma frase dita no vídeo é o que torna um acervo
    /// grande navegável.
    /// </summary>
    public static string ExtractText(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        var texto = new StringBuilder();

        foreach (var bruta in content.Replace("\r\n", "\n").Split('\n'))
        {
            var linha = bruta.Trim();

            if (linha.Length == 0)
                continue;

            if (linha.StartsWith("WEBVTT", StringComparison.Ordinal) ||
                linha.StartsWith("NOTE", StringComparison.Ordinal) ||
                linha.StartsWith("STYLE", StringComparison.Ordinal) ||
                linha.StartsWith("REGION", StringComparison.Ordinal))
                continue;

            // Linha de tempo ("00:00:01.000 --> 00:00:03.000") e numeração de bloco.
            if (linha.Contains("-->", StringComparison.Ordinal) || linha.All(char.IsAsciiDigit))
                continue;

            var limpa = RemoverMarcacao(linha);

            if (limpa.Length == 0)
                continue;

            if (texto.Length > 0)
                texto.Append(' ');

            texto.Append(limpa);
        }

        return texto.ToString();
    }

    /// <summary>Remove marcação de estilo do tipo <c>&lt;v Locutor&gt;</c> ou <c>&lt;i&gt;</c>.</summary>
    private static string RemoverMarcacao(string linha)
    {
        var resultado = new StringBuilder(linha.Length);
        var dentro = false;

        foreach (var caractere in linha)
        {
            if (caractere == '<')
            {
                dentro = true;
                continue;
            }

            if (caractere == '>')
            {
                dentro = false;
                continue;
            }

            if (!dentro)
                resultado.Append(caractere);
        }

        return resultado.ToString().Trim();
    }
}
