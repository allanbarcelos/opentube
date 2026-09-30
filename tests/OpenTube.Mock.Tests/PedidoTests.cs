// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Enums;
using OpenTube.Mock;

namespace OpenTube.Mock.Tests;

public class PedidoTests
{
    [Fact]
    public void Sem_argumento_usa_cinco_soltos_e_cinco_colecoes_de_tres()
    {
        var pedido = Pedido.Analisar([]);

        Assert.Equal(5, pedido.VideosSoltos);
        Assert.Equal(5, pedido.Colecoes);
        Assert.Equal(3, pedido.MinimoPorColecao);
        Assert.Equal(3, pedido.MaximoPorColecao);
        Assert.Equal("5 vídeos soltos e 5 coleções com 3 vídeos cada.", pedido.Resumo());
    }

    [Fact]
    public void Faixa_separa_quantidade_de_colecoes_e_videos_por_colecao()
    {
        var pedido = Pedido.Analisar(["--videos", "10", "--collections", "5.3-10"]);

        Assert.Equal(10, pedido.VideosSoltos);
        Assert.Equal(5, pedido.Colecoes);
        Assert.Equal(3, pedido.MinimoPorColecao);
        Assert.Equal(10, pedido.MaximoPorColecao);
        Assert.Equal("10 vídeos soltos e 5 coleções com 3 a 10 vídeos cada.", pedido.Resumo());
    }

    [Theory]
    [InlineData("--colections", "5.3-10")]
    [InlineData("--collections=5.3-10", null)]
    [InlineData("--colections=5.3-10", null)]
    public void Aceita_o_nome_collections_e_o_atalho_colections(string argumento, string? valor)
    {
        var args = valor is null ? new[] { argumento } : new[] { argumento, valor };
        var pedido = Pedido.Analisar(args);

        Assert.Equal(5, pedido.Colecoes);
        Assert.Equal(3, pedido.MinimoPorColecao);
        Assert.Equal(10, pedido.MaximoPorColecao);
        Assert.Equal(5, pedido.VideosSoltos);
    }

    [Fact]
    public void So_a_quantidade_de_colecoes_mantem_tres_videos()
    {
        var pedido = Pedido.Analisar(["--collections", "8"]);

        Assert.Equal(8, pedido.Colecoes);
        Assert.Equal(3, pedido.MinimoPorColecao);
        Assert.Equal(3, pedido.MaximoPorColecao);
    }

    [Fact]
    public void Ponto_sem_faixa_fixa_os_videos_da_colecao()
    {
        var pedido = Pedido.Analisar(["--videos", "0", "--collections", "2.4"]);

        Assert.Equal(0, pedido.VideosSoltos);
        Assert.Equal(2, pedido.Colecoes);
        Assert.Equal(4, pedido.MinimoPorColecao);
        Assert.Equal(4, pedido.MaximoPorColecao);
        Assert.Equal("nenhum vídeo solto e 2 coleções com 4 vídeos cada.", pedido.Resumo());
    }

    [Theory]
    [InlineData("--videos", "-1")]
    [InlineData("--collections", "5.10-3")]
    [InlineData("--collections", "5.0")]
    [InlineData("--collections", "abc")]
    [InlineData("--outra", "1")]
    public void Recusa_pedido_invalido(string nome, string valor)
    {
        Assert.Throws<UsoInvalidoException>(() => Pedido.Analisar([nome, valor]));
    }

    [Fact]
    public void Recusa_passar_do_teto()
    {
        var erro = Assert.Throws<UsoInvalidoException>(() => Pedido.Analisar(["--videos", "501"]));

        Assert.Contains("500", erro.Message);
    }
}

public class CatalogoTests
{
    [Fact]
    public void Padrao_tem_capa_nome_colecao_restrita_e_video_privado()
    {
        var plano = Catalogo.Montar(Pedido.Analisar([]), new Random(1));

        Assert.Equal(20, plano.Videos.Count);
        Assert.Equal(5, plano.Colecoes.Count);
        Assert.All(plano.Colecoes, c => Assert.Equal(3, c.Videos.Count));

        Assert.NotNull(plano.Colecoes[0].CorDaCapa);
        Assert.False(plano.Colecoes[0].RestritaAoConvidado);
        Assert.Null(plano.Colecoes[1].CorDaCapa);
        Assert.False(plano.Colecoes[1].RestritaAoConvidado);
        Assert.True(plano.Colecoes[4].RestritaAoConvidado);
        Assert.NotNull(plano.Colecoes[4].CorDaCapa);

        Assert.Equal(VideoVisibility.Restricted, plano.Videos.Single(v => v.Slug == plano.Colecoes[4].Videos[0]).Visibilidade);
        Assert.Equal(VideoVisibility.Public, plano.Videos.Single(v => v.Slug == "mock-video-01").Visibilidade);
        Assert.Equal(VideoVisibility.Private, plano.Videos.Single(v => v.Slug == "mock-video-05").Visibilidade);
        Assert.Equal("Série 2", plano.Colecoes[1].Nome);
        Assert.Equal("Série 10", Catalogo.Montar(Pedido.Analisar(["--collections", "10"]), new Random(1)).Colecoes[9].Nome);
        Assert.Contains(plano.Videos, v => v.Capitulos.Count == 2);
    }

    [Fact]
    public void Faixa_sorteia_um_tamanho_dentro_do_intervalo_para_cada_colecao()
    {
        var plano = Catalogo.Montar(Pedido.Analisar(["--videos", "2", "--collections", "4.3-10"]), new Random(7));

        Assert.Equal(4, plano.Colecoes.Count);
        Assert.All(plano.Colecoes, c => Assert.InRange(c.Videos.Count, 3, 10));
        Assert.Equal(2, plano.Videos.Count(v => v.Slug.StartsWith("mock-video-", StringComparison.Ordinal)));
        Assert.Equal(plano.Videos.Count, plano.Videos.Select(v => v.Slug).Distinct().Count());
    }

    [Fact]
    public void Uma_colecao_fica_publica_e_com_capa()
    {
        var plano = Catalogo.Montar(Pedido.Analisar(["--videos", "1", "--collections", "1"]), new Random(1));

        Assert.Single(plano.Colecoes);
        Assert.NotNull(plano.Colecoes[0].CorDaCapa);
        Assert.False(plano.Colecoes[0].RestritaAoConvidado);
        Assert.Equal(VideoVisibility.Public, Assert.Single(plano.Videos, v => v.Slug.StartsWith("mock-video-", StringComparison.Ordinal)).Visibilidade);
    }
}
