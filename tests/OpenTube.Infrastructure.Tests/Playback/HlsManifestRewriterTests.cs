// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Infrastructure.Playback;

namespace OpenTube.Infrastructure.Tests.Playback;

public class HlsManifestRewriterTests
{
    private const string Master = """
        #EXTM3U
        #EXT-X-VERSION:7
        #EXT-X-STREAM-INF:BANDWIDTH=985000,RESOLUTION=640x360,CODECS="avc1.4d401e,mp4a.40.2"
        360p/stream.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=1683000,RESOLUTION=854x480,CODECS="avc1.4d401f,mp4a.40.2"
        480p/stream.m3u8
        """;

    private const string Rendition = """
        #EXTM3U
        #EXT-X-VERSION:7
        #EXT-X-TARGETDURATION:4
        #EXT-X-MAP:URI="init-360p.mp4"
        #EXTINF:4.000000,
        seg-00000.m4s
        #EXTINF:4.000000,
        seg-00001.m4s
        #EXT-X-ENDLIST
        """;

    [Fact]
    public void Reescreve_as_variantes_da_playlist_principal()
    {
        var resultado = HlsManifestRewriter.Rewrite(Master, uri => "https://exemplo/" + uri);

        Assert.Contains("https://exemplo/360p/stream.m3u8", resultado);
        Assert.Contains("https://exemplo/480p/stream.m3u8", resultado);
    }

    [Fact]
    public void Preserva_as_marcacoes_e_a_ordem()
    {
        var resultado = HlsManifestRewriter.Rewrite(Master, _ => "x");

        Assert.StartsWith("#EXTM3U", resultado);
        Assert.Contains("RESOLUTION=640x360", resultado);
        Assert.Equal(Master.Split('\n').Length, resultado.Split('\n').Length);
    }

    [Fact]
    public void Reescreve_o_arquivo_de_inicializacao_dentro_do_atributo()
    {
        var resultado = HlsManifestRewriter.Rewrite(Rendition, uri => "https://exemplo/" + uri + "?assinatura");

        // O arquivo de inicialização vem num atributo, não numa linha solta: deixá-lo de
        // fora faria o player pedir um endereço sem assinatura e receber recusa.
        Assert.Contains("#EXT-X-MAP:URI=\"https://exemplo/init-360p.mp4?assinatura\"", resultado);
    }

    [Fact]
    public void Reescreve_todos_os_segmentos()
    {
        var resultado = HlsManifestRewriter.Rewrite(Rendition, uri => "https://exemplo/" + uri);

        Assert.Contains("https://exemplo/seg-00000.m4s", resultado);
        Assert.Contains("https://exemplo/seg-00001.m4s", resultado);
    }

    [Fact]
    public void Nao_mexe_nas_marcacoes_sem_endereco()
    {
        var resultado = HlsManifestRewriter.Rewrite(Rendition, _ => "SUBSTITUIDO");

        Assert.Contains("#EXT-X-TARGETDURATION:4", resultado);
        Assert.Contains("#EXTINF:4.000000,", resultado);
        Assert.Contains("#EXT-X-ENDLIST", resultado);
    }

    [Fact]
    public void Descarta_linhas_em_branco_sem_quebrar_o_arquivo()
    {
        var resultado = HlsManifestRewriter.Rewrite("#EXTM3U\n\nseg.m4s\n", uri => uri.ToUpperInvariant());

        Assert.Contains("SEG.M4S", resultado);
    }

    [Fact]
    public void Lida_com_quebra_de_linha_no_estilo_windows()
    {
        var resultado = HlsManifestRewriter.Rewrite("#EXTM3U\r\nseg.m4s\r\n", uri => "https://exemplo/" + uri);

        Assert.Contains("https://exemplo/seg.m4s", resultado);
        Assert.DoesNotContain("\r", resultado);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Playlist_vazia_volta_intacta(string manifesto)
    {
        Assert.Equal(manifesto, HlsManifestRewriter.Rewrite(manifesto, _ => "x"));
    }

    [Fact]
    public void Exige_a_funcao_de_reescrita()
    {
        Assert.Throws<ArgumentNullException>(() => HlsManifestRewriter.Rewrite(Master, null!));
    }

    [Theory]
    [InlineData("360p/stream.m3u8", "360p")]
    [InlineData("1080p/stream.m3u8", "1080p")]
    public void Extrai_a_versao_do_endereco_da_variante(string uri, string esperado)
    {
        Assert.Equal(esperado, HlsManifestRewriter.RenditionFromVariantUri(uri));
    }

    [Theory]
    [InlineData("seg-00000.m4s")]
    [InlineData("a/b/stream.m3u8")]
    [InlineData("360p/stream.mp4")]
    [InlineData("")]
    [InlineData(null)]
    public void Devolve_nulo_para_endereco_que_nao_e_variante(string? uri)
    {
        Assert.Null(HlsManifestRewriter.RenditionFromVariantUri(uri!));
    }
}
