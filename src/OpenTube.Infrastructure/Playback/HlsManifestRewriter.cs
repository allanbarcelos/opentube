using System.Text;

namespace OpenTube.Infrastructure.Playback;

/// <summary>
/// Reescreve os endereços de uma playlist HLS. O arquivo gerado pelo FFmpeg aponta para
/// caminhos relativos dentro do storage; o que chega ao navegador precisa apontar para
/// endereços que passam pela autorização ou que já vêm assinados.
/// </summary>
public static class HlsManifestRewriter
{
    /// <summary>
    /// Aplica <paramref name="map"/> a cada endereço da playlist: tanto as linhas soltas
    /// (segmentos e variantes) quanto os atributos <c>URI="…"</c> de marcações como
    /// <c>EXT-X-MAP</c>, que apontam para o arquivo de inicialização.
    /// </summary>
    public static string Rewrite(string manifest, Func<string, string> map)
    {
        ArgumentNullException.ThrowIfNull(map);

        if (string.IsNullOrWhiteSpace(manifest))
            return manifest ?? string.Empty;

        var resultado = new StringBuilder(manifest.Length * 2);
        var primeira = true;

        foreach (var linha in manifest.Split('\n'))
        {
            if (!primeira)
                resultado.Append('\n');
            primeira = false;

            var conteudo = linha.TrimEnd('\r');

            if (conteudo.Length == 0)
                continue;

            if (conteudo.StartsWith('#'))
            {
                resultado.Append(ReescreverAtributo(conteudo, map));
                continue;
            }

            resultado.Append(map(conteudo.Trim()));
        }

        return resultado.ToString();
    }

    private static string ReescreverAtributo(string linha, Func<string, string> map)
    {
        const string marcador = "URI=\"";

        var inicio = linha.IndexOf(marcador, StringComparison.Ordinal);
        if (inicio < 0)
            return linha;

        var valorInicio = inicio + marcador.Length;
        var valorFim = linha.IndexOf('"', valorInicio);

        if (valorFim < 0)
            return linha;

        var original = linha[valorInicio..valorFim];

        return string.Concat(linha.AsSpan(0, valorInicio), map(original), linha.AsSpan(valorFim));
    }

    /// <summary>
    /// Extrai o nome da versão a partir de um endereço de variante ("720p/stream.m3u8").
    /// Devolve <c>null</c> quando o endereço não tem o formato esperado.
    /// </summary>
    public static string? RenditionFromVariantUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        var partes = uri.Trim().Split('/');

        return partes.Length == 2 && partes[1].EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            ? partes[0]
            : null;
    }
}
