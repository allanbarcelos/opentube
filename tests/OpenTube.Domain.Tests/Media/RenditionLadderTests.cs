// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Media;

namespace OpenTube.Domain.Tests.Media;

public class RenditionLadderTests
{
    [Fact]
    public void Gera_as_quatro_versoes_para_um_original_em_1080p()
    {
        var ladder = RenditionLadder.For(1920, 1080);

        Assert.Equal(["360p", "480p", "720p", "1080p"], ladder.Select(r => r.Name));
        Assert.Equal("1920x1080", ladder[^1].Resolution);
    }

    [Fact]
    public void Nunca_aumenta_a_resolucao_do_original()
    {
        var ladder = RenditionLadder.For(1280, 720);

        Assert.Equal(["360p", "480p", "720p"], ladder.Select(r => r.Name));
        Assert.DoesNotContain(ladder, r => r.Height > 720);
    }

    [Fact]
    public void Mantem_a_proporcao_do_original()
    {
        var ladder = RenditionLadder.For(1920, 1080);

        foreach (var rendition in ladder)
        {
            var proporcao = (double)rendition.Width / rendition.Height;
            Assert.InRange(proporcao, 1.76, 1.79);
        }
    }

    [Fact]
    public void Trata_video_em_pe_pelo_menor_lado()
    {
        // 1080x1920 é um vídeo de celular: tem 1920 pixels, mas é um 1080p em pé.
        var ladder = RenditionLadder.For(1080, 1920);

        Assert.Equal(["360p", "480p", "720p", "1080p"], ladder.Select(r => r.Name));
        Assert.Equal("1080x1920", ladder[^1].Resolution);
        Assert.Equal("360x640", ladder[0].Resolution);
    }

    [Fact]
    public void Trata_video_quadrado()
    {
        var ladder = RenditionLadder.For(720, 720);

        Assert.Equal(["360p", "480p", "720p"], ladder.Select(r => r.Name));
        Assert.All(ladder, r => Assert.Equal(r.Width, r.Height));
    }

    [Fact]
    public void Gera_versao_unica_para_original_menor_que_o_degrau_mais_baixo()
    {
        var ladder = RenditionLadder.For(426, 240);

        var unica = Assert.Single(ladder);
        Assert.Equal("240p", unica.Name);
        Assert.Equal("426x240", unica.Resolution);
        Assert.True(unica.VideoBitrateKbps < 800, "a taxa de bits deve acompanhar a resolução menor");
    }

    [Fact]
    public void Garante_dimensoes_pares_exigidas_pelo_codificador()
    {
        var ladder = RenditionLadder.For(1919, 1081);

        Assert.All(ladder, r =>
        {
            Assert.Equal(0, r.Width % 2);
            Assert.Equal(0, r.Height % 2);
        });
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-1, -1)]
    public void Recusa_dimensoes_invalidas(int largura, int altura)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RenditionLadder.For(largura, altura));
    }

    [Fact]
    public void Taxa_de_bits_cresce_junto_com_a_resolucao()
    {
        var ladder = RenditionLadder.For(1920, 1080);

        for (var i = 1; i < ladder.Count; i++)
            Assert.True(ladder[i].VideoBitrateKbps > ladder[i - 1].VideoBitrateKbps);
    }

    [Fact]
    public void Largura_de_banda_do_manifesto_soma_video_audio_e_sobrecarga()
    {
        var rendition = new Rendition("720p", 1280, 720, 2800, 128);

        Assert.True(rendition.ManifestBandwidthBps > (2800 + 128) * 1000);
        Assert.Equal(2996, rendition.MaxRateKbps);
        Assert.Equal(5600, rendition.BufferSizeKbps);
    }

    [Theory]
    [InlineData(30, 120)]
    [InlineData(25, 100)]
    [InlineData(59.94, 240)]
    public void Intervalo_de_quadros_chave_cobre_exatamente_o_segmento(double fps, int esperado)
    {
        Assert.Equal(esperado, RenditionLadder.KeyFrameInterval(fps));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(1000)]
    public void Assume_30_quadros_quando_a_taxa_informada_nao_faz_sentido(double fps)
    {
        Assert.Equal(120, RenditionLadder.KeyFrameInterval(fps));
    }
}
