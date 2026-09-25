using OpenTube.Worker.Media;

namespace OpenTube.Worker.Tests.Media;

public class SpriteLayoutTests
{
    [Fact]
    public void Video_curto_ganha_uma_miniatura_a_cada_dois_segundos()
    {
        var disposicao = SpriteLayout.For(60, 1920, 1080);

        Assert.Equal(2, disposicao.IntervalSeconds);
        Assert.Equal(30, disposicao.Count);
        Assert.Equal(3, disposicao.Rows);
    }

    [Fact]
    public void Video_longo_espaca_para_nao_passar_do_teto()
    {
        var disposicao = SpriteLayout.For(7200, 1920, 1080);

        Assert.True(disposicao.Count <= SpriteLayout.MaxThumbs);
        Assert.Equal(72, disposicao.IntervalSeconds);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(300)]
    [InlineData(3600)]
    [InlineData(14400)]
    public void Nunca_passa_do_teto_de_miniaturas(double duracao)
    {
        Assert.InRange(SpriteLayout.For(duracao, 1280, 720).Count, 1, SpriteLayout.MaxThumbs);
    }

    [Fact]
    public void A_miniatura_mantem_a_proporcao_do_video()
    {
        var disposicao = SpriteLayout.For(120, 1920, 1080);

        Assert.Equal(160, disposicao.ThumbWidth);
        Assert.Equal(90, disposicao.ThumbHeight);
    }

    [Fact]
    public void A_miniatura_de_video_em_pe_fica_mais_alta_que_larga()
    {
        var disposicao = SpriteLayout.For(120, 1080, 1920);

        Assert.True(disposicao.ThumbHeight > disposicao.ThumbWidth);
        Assert.Equal(0, disposicao.ThumbHeight % 2);
    }

    [Fact]
    public void Video_muito_curto_ainda_gera_uma_miniatura()
    {
        var disposicao = SpriteLayout.For(1, 640, 360);

        Assert.Equal(1, disposicao.Count);
        Assert.Equal(1, disposicao.Columns);
        Assert.Equal(1, disposicao.Rows);
    }

    [Fact]
    public void As_miniaturas_ficam_lado_a_lado_e_quebram_a_linha_no_limite()
    {
        var disposicao = SpriteLayout.For(60, 1920, 1080);

        Assert.Equal((0, 0), disposicao.PositionOf(0));
        Assert.Equal((160, 0), disposicao.PositionOf(1));
        Assert.Equal((0, 90), disposicao.PositionOf(10));
        Assert.Equal((160, 90), disposicao.PositionOf(11));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(30)]
    public void Recusa_posicao_fora_da_folha(int indice)
    {
        var disposicao = SpriteLayout.For(60, 1920, 1080);

        Assert.Throws<ArgumentOutOfRangeException>(() => disposicao.PositionOf(indice));
    }

    [Theory]
    [InlineData(0, 1920, 1080)]
    [InlineData(60, 0, 1080)]
    [InlineData(60, 1920, 0)]
    public void Recusa_parametros_invalidos(double duracao, int largura, int altura)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpriteLayout.For(duracao, largura, altura));
    }
}

public class SpriteVttTests
{
    [Fact]
    public void Abre_com_o_cabecalho_do_formato()
    {
        var vtt = SpriteVtt.Build(SpriteLayout.For(60, 1920, 1080), "sprite.jpg", 60);

        Assert.StartsWith("WEBVTT", vtt);
    }

    [Fact]
    public void Cada_trecho_aponta_para_um_recorte_da_folha()
    {
        var disposicao = SpriteLayout.For(60, 1920, 1080);

        var vtt = SpriteVtt.Build(disposicao, "sprite.jpg", 60);

        Assert.Contains("00:00:00.000 --> 00:00:02.000", vtt);
        Assert.Contains("sprite.jpg#xywh=0,0,160,90", vtt);
        Assert.Contains("sprite.jpg#xywh=160,0,160,90", vtt);
        Assert.Equal(disposicao.Count, vtt.Split("#xywh=").Length - 1);
    }

    [Fact]
    public void O_ultimo_trecho_nao_passa_do_fim_do_video()
    {
        var vtt = SpriteVtt.Build(SpriteLayout.For(59, 1920, 1080), "sprite.jpg", 59);

        Assert.Contains("--> 00:00:59.000", vtt);
        Assert.DoesNotContain("--> 00:01:00.000", vtt);
    }

    [Theory]
    [InlineData(0, "00:00:00.000")]
    [InlineData(1.5, "00:00:01.500")]
    [InlineData(61.25, "00:01:01.250")]
    [InlineData(3661, "01:01:01.000")]
    [InlineData(-5, "00:00:00.000")]
    public void Formata_o_instante_no_padrao_do_formato(double segundos, string esperado)
    {
        Assert.Equal(esperado, SpriteVtt.Tempo(segundos));
    }

    [Fact]
    public void Exige_o_nome_da_folha()
    {
        Assert.ThrowsAny<ArgumentException>(() => SpriteVtt.Build(SpriteLayout.For(60, 1920, 1080), "", 60));
    }
}
