// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Globalization;

namespace OpenTube.Mock;

/// <summary>Quanto gerar. <c>5.3-10</c> são 5 coleções, cada uma com 3 a 10 vídeos.</summary>
internal sealed record Pedido(int VideosSoltos, int Colecoes, int MinimoPorColecao, int MaximoPorColecao)
{
    public const int VideosPadrao = 5;

    public const int ColecoesPadrao = 5;

    public const int PorColecaoPadrao = 3;

    /// <summary>Teto da soma, para um número alto não encher o disco de desenvolvimento.</summary>
    public const int Teto = 500;

    public static string Ajuda { get; } =
        """
        make mock
            5 vídeos soltos e 5 coleções com 3 vídeos cada.

        make mock videos=10 collections=5.3-10
            10 vídeos soltos e 5 coleções, cada uma com 3 a 10 vídeos.

        videos=N               vídeos fora de coleção (padrão 5)
        collections=N          N coleções com 3 vídeos
        collections=N.M        N coleções com M vídeos
        collections=N.M-K      N coleções, cada uma com M a K vídeos
        """;

    public string Resumo()
    {
        var soltos = VideosSoltos switch
        {
            0 => "nenhum vídeo solto",
            1 => "1 vídeo solto",
            _ => $"{VideosSoltos} vídeos soltos"
        };

        if (Colecoes == 0)
            return $"{soltos} e nenhuma coleção.";

        var colecoes = Colecoes == 1 ? "1 coleção" : $"{Colecoes} coleções";
        var faixa = MinimoPorColecao == MaximoPorColecao
            ? $"{MinimoPorColecao} {(MinimoPorColecao == 1 ? "vídeo" : "vídeos")}"
            : $"{MinimoPorColecao} a {MaximoPorColecao} vídeos";
        var cada = Colecoes == 1 ? "" : " cada";

        return $"{soltos} e {colecoes} com {faixa}{cada}.";
    }

    public static Pedido Analisar(string[] args)
    {
        int? videos = null;
        string? colecoes = null;

        for (var i = 0; i < args.Length; i++)
        {
            var argumento = args[i];

            if (argumento is "--videos")
            {
                videos = Inteiro(Proximo(args, ref i, "--videos"), "--videos");
                continue;
            }

            if (argumento.StartsWith("--videos=", StringComparison.Ordinal))
            {
                videos = Inteiro(argumento["--videos=".Length..], "--videos");
                continue;
            }

            if (argumento is "--collections" or "--colections")
            {
                colecoes = Unico(colecoes, Proximo(args, ref i, "--collections"));
                continue;
            }

            if (argumento.StartsWith("--collections=", StringComparison.Ordinal)
                || argumento.StartsWith("--colections=", StringComparison.Ordinal))
            {
                colecoes = Unico(colecoes, argumento[(argumento.IndexOf('=') + 1)..]);
                continue;
            }

            throw new UsoInvalidoException($"Argumento não reconhecido: {argumento}{Environment.NewLine}{Ajuda}");
        }

        var (quantidade, minimo, maximo) = InterpretarColecoes(colecoes);
        var soltos = videos ?? VideosPadrao;
        ConferirTeto(soltos, quantidade, maximo);

        return new Pedido(soltos, quantidade, minimo, maximo);
    }

    private static (int Quantidade, int Minimo, int Maximo) InterpretarColecoes(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
            return (ColecoesPadrao, PorColecaoPadrao, PorColecaoPadrao);

        var partes = texto.Trim().Split('.', 2);
        var quantidade = Inteiro(partes[0], "collections");

        if (partes.Length == 1)
            return (quantidade, PorColecaoPadrao, PorColecaoPadrao);

        var faixa = partes[1].Split('-', 2);
        var minimo = Inteiro(faixa[0], "vídeos por coleção");
        var maximo = faixa.Length == 1 ? minimo : Inteiro(faixa[1], "vídeos por coleção");

        if (quantidade > 0 && minimo < 1)
            throw new UsoInvalidoException("Uma coleção precisa de ao menos 1 vídeo.");

        if (minimo > maximo)
            throw new UsoInvalidoException($"A faixa {minimo}-{maximo} está invertida. Exemplo: 5.3-10.");

        return (quantidade, minimo, maximo);
    }

    private static void ConferirTeto(int soltos, int colecoes, int maximoPorColecao)
    {
        if (soltos < 0)
            throw new UsoInvalidoException("A quantidade de vídeos soltos não pode ser negativa.");

        var total = (long)soltos + (long)colecoes * maximoPorColecao;

        if (total > Teto)
        {
            throw new UsoInvalidoException(
                $"Isso geraria até {total} vídeos. O teto de make mock é {Teto}.");
        }
    }

    private static string Unico(string? atual, string valor)
    {
        if (atual is not null)
            throw new UsoInvalidoException("Informe collections uma vez só.");

        return valor;
    }

    private static string Proximo(string[] args, ref int indice, string nome)
    {
        if (indice + 1 >= args.Length || args[indice + 1].StartsWith('-'))
            throw new UsoInvalidoException($"Faltou o valor de {nome}.");

        return args[++indice];
    }

    private static int Inteiro(string texto, string nome)
    {
        if (int.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out var valor) && valor >= 0)
            return valor;

        throw new UsoInvalidoException($"Valor inválido para {nome}: '{texto}'.");
    }
}

internal sealed class UsoInvalidoException(string message) : Exception(message);
