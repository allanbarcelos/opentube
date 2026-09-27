// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Globalization;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Localization;

namespace OpenTube.Web.Components.Shared;

/// <summary>Como cada posição da marca d'água do acervo aparece na página e no formulário.</summary>
public static class PosicaoDaMarca
{
    /// <summary>Ordem de exibição no formulário, como numa grade: cantos de cima, centro, cantos de baixo.</summary>
    public static readonly WatermarkPosition[] Todas =
    [
        WatermarkPosition.TopLeft, WatermarkPosition.TopRight, WatermarkPosition.Center,
        WatermarkPosition.BottomLeft, WatermarkPosition.BottomRight
    ];

    /// <summary>Posição sugerida quando ainda não há marca: onde menos atrapalha.</summary>
    public const WatermarkPosition Padrao = WatermarkPosition.BottomRight;

    // Medidas de exibição, relativas ao vídeo, seguindo a prática de TV e streaming para o
    // logotipo permanente no canto (o "bug" ou DOG): visível sem competir com o conteúdo.

    /// <summary>
    /// Largura da área do logotipo, em % da largura do vídeo. Emissoras e plataformas usam de
    /// 5% a 10%; 10% mantém legível um logotipo com texto num player pequeno.
    /// </summary>
    public const int LarguraPercentual = 10;

    /// <summary>
    /// Altura da área, em % da altura do vídeo. Num quadro 16:9 dá uma área de cerca de 3:2:
    /// logotipos horizontais são limitados pela largura, e os quadrados, pela altura.
    /// </summary>
    public const int AlturaPercentual = 12;

    /// <summary>
    /// Distância das bordas, em %: a área segura de títulos (EBU R95, SMPTE ST 2046-1), para o
    /// logotipo não ser cortado por telas que recortam a borda nem ficar colado nela.
    /// </summary>
    public const int MargemPercentual = 5;

    /// <summary>Nos cantos de baixo, a margem sobe para o logotipo não ficar sob a barra de controles.</summary>
    public const int MargemInferiorPercentual = 12;

    /// <summary>Opacidade: presente sem pesar sobre a imagem.</summary>
    public const double Opacidade = 0.75;

    /// <summary>
    /// Estilo da área do logotipo. A imagem se ajusta a ela sem distorcer, então todo logotipo
    /// aparece no mesmo tamanho relativo, qualquer que seja o arquivo enviado.
    /// </summary>
    public static string Estilo(WatermarkPosition posicao)
    {
        var lugar = posicao switch
        {
            WatermarkPosition.TopLeft => $"top:{MargemPercentual}%;left:{MargemPercentual}%",
            WatermarkPosition.TopRight => $"top:{MargemPercentual}%;right:{MargemPercentual}%",
            WatermarkPosition.BottomLeft => $"bottom:{MargemInferiorPercentual}%;left:{MargemPercentual}%",
            WatermarkPosition.Center => "top:50%;left:50%;transform:translate(-50%,-50%)",
            _ => $"bottom:{MargemInferiorPercentual}%;right:{MargemPercentual}%"
        };

        return string.Create(CultureInfo.InvariantCulture,
            $"width:{LarguraPercentual}%;height:{AlturaPercentual}%;opacity:{Opacidade};{lugar}");
    }

    /// <summary>Classe que posiciona a imagem sobre o vídeo.</summary>
    public static string Classe(WatermarkPosition posicao) => "marca-logo-" + Sufixo(posicao);

    /// <summary>Classe que coloca a opção do formulário na grade, no lugar que ela representa.</summary>
    public static string Opcao(WatermarkPosition posicao) => "marca-opcao-" + Sufixo(posicao);

    private static string Sufixo(WatermarkPosition posicao) => posicao switch
    {
        WatermarkPosition.TopLeft => "top-left",
        WatermarkPosition.TopRight => "top-right",
        WatermarkPosition.BottomLeft => "bottom-left",
        WatermarkPosition.Center => "center",
        _ => "bottom-right"
    };

    public static string Rotulo(WatermarkPosition posicao) => LocalText.Get(posicao switch
    {
        WatermarkPosition.TopLeft => "Top left",
        WatermarkPosition.TopRight => "Top right",
        WatermarkPosition.BottomLeft => "Bottom left",
        WatermarkPosition.Center => "Center",
        _ => "Bottom right"
    });
}
