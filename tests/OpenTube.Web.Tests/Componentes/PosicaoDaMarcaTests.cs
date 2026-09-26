using System.Globalization;
using OpenTube.Domain.Enums;
using OpenTube.Web.Components.Shared;

namespace OpenTube.Web.Tests.Componentes;

/// <summary>
/// Medidas de exibição da marca d'água do acervo: uma área de tamanho padrão, dentro da área
/// segura de títulos, igual para qualquer logotipo enviado.
/// </summary>
public class PosicaoDaMarcaTests
{
    [Theory]
    [InlineData(WatermarkPosition.TopLeft, "top:5%;left:5%")]
    [InlineData(WatermarkPosition.TopRight, "top:5%;right:5%")]
    [InlineData(WatermarkPosition.BottomLeft, "bottom:12%;left:5%")]
    [InlineData(WatermarkPosition.BottomRight, "bottom:12%;right:5%")]
    [InlineData(WatermarkPosition.Center, "top:50%;left:50%;transform:translate(-50%,-50%)")]
    public void Cada_posicao_usa_a_mesma_area_padrao_na_margem_segura(WatermarkPosition posicao, string lugar)
    {
        Assert.Equal($"width:10%;height:12%;opacity:0.75;{lugar}", PosicaoDaMarca.Estilo(posicao));
    }

    [Fact]
    public void A_area_segue_a_faixa_usada_por_emissoras_e_plataformas()
    {
        // Logotipo permanente de canto: de 5% a 10% da largura do quadro, dentro da área
        // segura de títulos (5% das bordas), com opacidade parcial.
        Assert.InRange(PosicaoDaMarca.LarguraPercentual, 5, 10);
        Assert.Equal(5, PosicaoDaMarca.MargemPercentual);
        Assert.InRange(PosicaoDaMarca.Opacidade, 0.5, 0.9);

        // Numa tela 16:9, a área tem cerca de 3:2: comporta logotipos horizontais e quadrados.
        var proporcao = PosicaoDaMarca.LarguraPercentual * 16.0 / (PosicaoDaMarca.AlturaPercentual * 9.0);
        Assert.InRange(proporcao, 1.3, 1.7);
    }

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("fr-FR")]
    public void O_estilo_nao_depende_do_idioma_da_pagina(string cultura)
    {
        // Em português e em francês o decimal é vírgula; "opacity:0,75" seria CSS inválido.
        var anterior = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(cultura);

        try
        {
            Assert.Contains("opacity:0.75;", PosicaoDaMarca.Estilo(WatermarkPosition.TopLeft));
        }
        finally
        {
            CultureInfo.CurrentCulture = anterior;
        }
    }

    [Fact]
    public void Todas_as_posicoes_tem_classe_e_rotulo_proprios()
    {
        Assert.Equal(Enum.GetValues<WatermarkPosition>().Order(), PosicaoDaMarca.Todas.Order());
        Assert.Equal(5, PosicaoDaMarca.Todas.Select(PosicaoDaMarca.Classe).Distinct().Count());
        Assert.Equal(5, PosicaoDaMarca.Todas.Select(PosicaoDaMarca.Rotulo).Distinct().Count());
    }
}
