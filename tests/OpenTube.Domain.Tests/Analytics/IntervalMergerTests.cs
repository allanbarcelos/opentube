using OpenTube.Domain.Analytics;

namespace OpenTube.Domain.Tests.Analytics;

public class WatchIntervalTests
{
    [Fact]
    public void Ordena_o_trecho_quando_vem_invertido()
    {
        var trecho = WatchInterval.Create(90, 30, 600);

        Assert.Equal(30, trecho.Start);
        Assert.Equal(90, trecho.End);
        Assert.Equal(60, trecho.Duration);
    }

    [Fact]
    public void Limita_o_trecho_a_duracao_do_video()
    {
        // O player pode relatar uma posição além do fim por arredondamento.
        var trecho = WatchInterval.Create(-5, 700, 600);

        Assert.Equal(0, trecho.Start);
        Assert.Equal(600, trecho.End);
    }

    [Fact]
    public void Sem_duracao_conhecida_o_trecho_nao_e_cortado()
    {
        Assert.Equal(40, WatchInterval.Create(10, 50, 0).Duration);
    }

    [Fact]
    public void Trecho_de_tamanho_zero_e_vazio()
    {
        Assert.True(new WatchInterval(10, 10).IsEmpty);
        Assert.False(new WatchInterval(10, 11).IsEmpty);
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(19.9, true)]
    [InlineData(20, false)]
    [InlineData(9.9, false)]
    public void Reconhece_a_posicao_dentro_do_trecho(double posicao, bool esperado)
    {
        Assert.Equal(esperado, new WatchInterval(10, 20).Contains(posicao));
    }
}

public class IntervalMergerTests
{
    [Fact]
    public void Sem_trechos_o_resultado_e_vazio()
    {
        Assert.Empty(IntervalMerger.Merge([]));
        Assert.Equal(0, IntervalMerger.UniqueSeconds([]));
    }

    [Fact]
    public void Trechos_separados_continuam_separados()
    {
        var trechos = IntervalMerger.Merge([new(0, 10), new(100, 120)]);

        Assert.Equal(2, trechos.Count);
        Assert.Equal(30, IntervalMerger.UniqueSeconds([new(0, 10), new(100, 120)]));
    }

    [Fact]
    public void Trechos_sobrepostos_viram_um_so()
    {
        var unico = Assert.Single(IntervalMerger.Merge([new(0, 30), new(20, 50)]));

        Assert.Equal(new WatchInterval(0, 50), unico);
    }

    [Fact]
    public void Reassistir_o_mesmo_trecho_nao_dobra_o_tempo()
    {
        // O ponto inteiro da fusão: dez minutos vistos duas vezes são dez minutos.
        Assert.Equal(600, IntervalMerger.UniqueSeconds([new(0, 600), new(0, 600), new(100, 300)]));
    }

    [Fact]
    public void Trecho_contido_em_outro_desaparece()
    {
        var trechos = IntervalMerger.Merge([new(0, 100), new(20, 30)]);

        Assert.Single(trechos);
        Assert.Equal(100, trechos[0].Duration);
    }

    [Fact]
    public void Trechos_encostados_sao_unidos()
    {
        var trechos = IntervalMerger.Merge([new(0, 10), new(10, 20)]);

        Assert.Single(trechos);
        Assert.Equal(20, trechos[0].End);
    }

    [Fact]
    public void Lacuna_de_arredondamento_entre_batidas_nao_fragmenta_o_trecho()
    {
        // Duas batidas consecutivas do player podem deixar um décimo de segundo de folga.
        Assert.Single(IntervalMerger.Merge([new(0, 10), new(10.3, 20)]));
    }

    [Fact]
    public void Lacuna_de_verdade_e_preservada()
    {
        Assert.Equal(2, IntervalMerger.Merge([new(0, 10), new(15, 20)]).Count);
    }

    [Fact]
    public void A_entrada_fora_de_ordem_e_normalizada()
    {
        var trechos = IntervalMerger.Merge([new(100, 120), new(0, 10), new(50, 60)]);

        Assert.Equal([0d, 50, 100], trechos.Select(t => t.Start));
    }

    [Fact]
    public void Trechos_vazios_sao_descartados()
    {
        Assert.Single(IntervalMerger.Merge([new(10, 10), new(0, 5)]));
    }

    [Theory]
    [InlineData(600, 300, 0.5)]
    [InlineData(600, 600, 1)]
    [InlineData(600, 0, 0)]
    public void A_cobertura_e_a_fracao_do_video_assistida(double duracao, double assistido, double esperado)
    {
        var trechos = assistido > 0 ? new WatchInterval[] { new(0, assistido) } : [];

        Assert.Equal(esperado, IntervalMerger.Coverage(trechos, duracao), 3);
    }

    [Fact]
    public void A_cobertura_nao_passa_de_cem_por_cento()
    {
        Assert.Equal(1, IntervalMerger.Coverage([new(0, 900)], 600));
    }

    [Fact]
    public void Video_sem_duracao_conhecida_nao_divide_por_zero()
    {
        Assert.Equal(0, IntervalMerger.Coverage([new(0, 100)], 0));
    }

    [Fact]
    public void Exige_a_lista_de_trechos()
    {
        Assert.Throws<ArgumentNullException>(() => IntervalMerger.Merge(null!));
    }
}
