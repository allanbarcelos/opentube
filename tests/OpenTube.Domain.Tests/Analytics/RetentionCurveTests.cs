// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Analytics;

namespace OpenTube.Domain.Tests.Analytics;

public class RetentionCurveTests
{
    [Fact]
    public void Quem_assistiu_tudo_aparece_em_todas_as_fatias()
    {
        var curva = RetentionCurve.Build([[new WatchInterval(0, 600)]], 600, buckets: 10);

        Assert.All(curva, c => Assert.Equal(1, c));
    }

    [Fact]
    public void Quem_abandonou_na_metade_some_da_segunda_metade()
    {
        var curva = RetentionCurve.Build([[new WatchInterval(0, 300)]], 600, buckets: 10);

        Assert.Equal([1, 1, 1, 1, 1, 0, 0, 0, 0, 0], curva);
    }

    [Fact]
    public void A_curva_mostra_onde_o_publico_cai()
    {
        // Três pessoas: uma viu tudo, uma metade, uma só o começo.
        var curva = RetentionCurve.Build(
            [
                [new WatchInterval(0, 600)],
                [new WatchInterval(0, 300)],
                [new WatchInterval(0, 60)]
            ],
            600, buckets: 10);

        Assert.Equal(3, curva[0]);
        Assert.Equal(2, curva[2]);
        Assert.Equal(1, curva[9]);
    }

    [Fact]
    public void Quem_pulou_o_meio_nao_conta_naquele_trecho()
    {
        var curva = RetentionCurve.Build(
            [[new WatchInterval(0, 120), new WatchInterval(480, 600)]], 600, buckets: 10);

        Assert.Equal(1, curva[0]);
        Assert.Equal(0, curva[5]);
        Assert.Equal(1, curva[9]);
    }

    [Fact]
    public void Reassistir_nao_conta_duas_vezes_na_mesma_fatia()
    {
        var curva = RetentionCurve.Build(
            [[new WatchInterval(0, 600), new WatchInterval(0, 600)]], 600, buckets: 5);

        Assert.All(curva, c => Assert.Equal(1, c));
    }

    [Fact]
    public void Um_segundo_perdido_na_troca_de_qualidade_nao_derruba_a_fatia()
    {
        // Exigir a fatia inteira puniria quem assistiu tudo com uma falha momentânea.
        var curva = RetentionCurve.Build(
            [[new WatchInterval(0, 299), new WatchInterval(301, 600)]], 600, buckets: 10);

        Assert.All(curva, c => Assert.Equal(1, c));
    }

    [Fact]
    public void Sem_espectadores_a_curva_fica_zerada()
    {
        var curva = RetentionCurve.Build([], 600, buckets: 10);

        Assert.Equal(10, curva.Count);
        Assert.All(curva, c => Assert.Equal(0, c));
    }

    [Fact]
    public void Video_sem_duracao_conhecida_devolve_curva_zerada()
    {
        var curva = RetentionCurve.Build([[new WatchInterval(0, 100)]], 0, buckets: 10);

        Assert.All(curva, c => Assert.Equal(0, c));
    }

    [Fact]
    public void A_quantidade_padrao_e_uma_fatia_por_ponto_percentual()
    {
        Assert.Equal(100, RetentionCurve.Build([[new WatchInterval(0, 600)]], 600).Count);
    }

    [Fact]
    public void Recusa_quantidade_de_fatias_invalida()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetentionCurve.Build([], 600, buckets: 0));
    }

    [Fact]
    public void Converte_a_contagem_em_percentual()
    {
        var percentuais = RetentionCurve.AsPercentages([4, 2, 1, 0], totalViewers: 4);

        Assert.Equal([100d, 50, 25, 0], percentuais);
    }

    [Fact]
    public void Sem_espectadores_o_percentual_e_zero()
    {
        Assert.All(RetentionCurve.AsPercentages([3, 2], 0), p => Assert.Equal(0, p));
    }
}
