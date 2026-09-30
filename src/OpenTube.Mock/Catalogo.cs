// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Enums;
using OpenTube.Domain.Media;
using OpenTube.Domain.ValueObjects;

namespace OpenTube.Mock;

/// <summary>
/// Monta o catálogo a partir do pedido. Os nomes levam o número no título para a ordenação
/// natural (2 antes de 10). A segunda coleção fica sem capa; a partir da terceira, a última
/// é restrita. Havendo ao menos dois vídeos soltos, o último é privado.
/// </summary>
internal static class Catalogo
{
    public const string Prefixo = "mock-";

    /// <summary>Entra com o código que o Mailpit mostra. A concessão da coleção restrita aponta para este email.</summary>
    public const string Convidado = "convidado@empresa.test";

    public const string Nota = "Gerado por make mock";

    private static readonly string[] Cores =
    [
        "0x1d4ed8", "0x0f766e", "0xb45309", "0x7c3aed", "0xbe123c",
        "0x15803d", "0x65a30d", "0x334155", "0x0e7490", "0x9f1239",
        "0x1e40af", "0xc2410c", "0x0369a1", "0x4d7c0f", "0x6d28d9"
    ];

    private static readonly Chapter[] CapitulosIniciais =
    [
        new(0, "Abertura"),
        new(3, "O que vem a seguir")
    ];

    public static Plano Montar(Pedido pedido, Random? aleatorio = null)
    {
        var sorteio = aleatorio ?? Random.Shared;
        var videos = new List<VideoDeTeste>();
        var colecoes = new List<ColecaoDeTeste>();
        var capitulosPendentes = true;

        for (var indice = 1; indice <= pedido.Colecoes; indice++)
        {
            var quantidade = pedido.MinimoPorColecao == pedido.MaximoPorColecao
                ? pedido.MinimoPorColecao
                : sorteio.Next(pedido.MinimoPorColecao, pedido.MaximoPorColecao + 1);

            var restrita = pedido.Colecoes >= 3 && indice == pedido.Colecoes;
            var semCapa = pedido.Colecoes >= 2 && indice == 2;
            var slugs = new List<string>(quantidade);
            var cor = Cores[(indice - 1) % Cores.Length];

            for (var aula = 1; aula <= quantidade; aula++)
            {
                var slug = $"{Prefixo}serie-{Numero(indice, pedido.Colecoes)}-aula-{Numero(aula, quantidade)}";
                var visibilidade = restrita ? VideoVisibility.Restricted : VideoVisibility.Public;
                var capitulos = CapitulosDe(visibilidade, ref capitulosPendentes);

                videos.Add(new VideoDeTeste(
                    slug,
                    $"Aula {aula}",
                    capitulos.Count > 0
                        ? "Aula de exemplo, com dois capítulos."
                        : restrita
                            ? "Aula da coleção restrita ao convidado de teste."
                            : "Aula de exemplo.",
                    visibilidade,
                    Cores[(aula - 1) % Cores.Length],
                    restrita ? ["mock", "restrito"] : ["mock", "exemplo"],
                    capitulos));

                slugs.Add(slug);
            }

            colecoes.Add(new ColecaoDeTeste(
                $"{Prefixo}serie-{Numero(indice, pedido.Colecoes)}",
                $"Série {indice}",
                restrita
                    ? "Coleção restrita ao convidado de teste."
                    : semCapa
                        ? "Coleção pública. O cartão mostra o nome, porque ninguém enviou uma imagem."
                        : "Coleção pública com imagem de capa.",
                semCapa ? null : cor,
                restrita,
                slugs));
        }

        for (var indice = 1; indice <= pedido.VideosSoltos; indice++)
        {
            var privado = pedido.VideosSoltos >= 2 && indice == pedido.VideosSoltos;
            var visibilidade = privado ? VideoVisibility.Private : VideoVisibility.Public;
            var capitulos = CapitulosDe(visibilidade, ref capitulosPendentes);

            videos.Add(new VideoDeTeste(
                $"{Prefixo}video-{Numero(indice, pedido.VideosSoltos)}",
                $"Vídeo {indice}",
                capitulos.Count > 0
                    ? "Vídeo solto, com dois capítulos."
                    : privado
                        ? "Vídeo solto, visível só para administradores."
                        : "Vídeo solto, fora de coleção.",
                visibilidade,
                Cores[(indice - 1) % Cores.Length],
                privado ? ["mock", "interno"] : ["mock", "avulso"],
                capitulos));
        }

        var plano = new Plano(pedido, videos, colecoes);
        Validar(plano);
        return plano;
    }

    private static IReadOnlyList<Chapter> CapitulosDe(VideoVisibility visibilidade, ref bool pendentes)
    {
        if (!pendentes || visibilidade != VideoVisibility.Public)
            return [];

        pendentes = false;
        return CapitulosIniciais;
    }

    private static string Numero(int valor, int total)
    {
        var digitos = Math.Max(2, total.ToString().Length);
        return valor.ToString().PadLeft(digitos, '0');
    }

    private static void Validar(Plano plano)
    {
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        var emColecao = new HashSet<string>(StringComparer.Ordinal);

        foreach (var video in plano.Videos)
        {
            if (!SlugValido(video.Slug) || !slugs.Add(video.Slug))
                throw new InvalidOperationException($"Slug de vídeo inválido no catálogo: {video.Slug}");
        }

        foreach (var colecao in plano.Colecoes)
        {
            if (!SlugValido(colecao.Slug) || !slugs.Add(colecao.Slug))
                throw new InvalidOperationException($"Slug de coleção inválido no catálogo: {colecao.Slug}");

            if (colecao.Videos.Count == 0)
                throw new InvalidOperationException($"A coleção {colecao.Slug} não tem vídeos.");

            var esperada = colecao.RestritaAoConvidado ? VideoVisibility.Restricted : VideoVisibility.Public;

            foreach (var slug in colecao.Videos)
            {
                var video = plano.Videos.FirstOrDefault(v => v.Slug == slug)
                    ?? throw new InvalidOperationException($"A coleção {colecao.Slug} aponta para {slug}, que não está no catálogo.");

                if (video.Visibilidade != esperada)
                    throw new InvalidOperationException($"O vídeo {slug} não combina com a visibilidade da coleção {colecao.Slug}.");

                if (!emColecao.Add(slug))
                    throw new InvalidOperationException($"O vídeo {slug} está em mais de uma coleção.");
            }
        }

        foreach (var video in plano.Videos.Where(v => v.Visibilidade == VideoVisibility.Private))
        {
            if (emColecao.Contains(video.Slug))
                throw new InvalidOperationException($"O vídeo privado {video.Slug} não pode entrar numa coleção.");
        }
    }

    private static bool SlugValido(string slug) =>
        slug.StartsWith(Prefixo, StringComparison.Ordinal)
        && slug.Length <= Slug.MaxLength
        && slug.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}

internal sealed record Plano(
    Pedido Pedido,
    IReadOnlyList<VideoDeTeste> Videos,
    IReadOnlyList<ColecaoDeTeste> Colecoes);

/// <param name="Cor">Cor da capa, no formato do FFmpeg (<c>0xRRGGBB</c>).</param>
internal sealed record VideoDeTeste(
    string Slug,
    string Titulo,
    string Descricao,
    VideoVisibility Visibilidade,
    string Cor,
    IReadOnlyList<string> Tags,
    IReadOnlyList<Chapter> Capitulos);

/// <param name="CorDaCapa">Nula mantém a capa padrão, que é o nome da coleção.</param>
internal sealed record ColecaoDeTeste(
    string Slug,
    string Nome,
    string Descricao,
    string? CorDaCapa,
    bool RestritaAoConvidado,
    IReadOnlyList<string> Videos);
