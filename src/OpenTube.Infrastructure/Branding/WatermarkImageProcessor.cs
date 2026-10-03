// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Entities;
using SkiaSharp;

namespace OpenTube.Infrastructure.Branding;

/// <summary>
/// Leva a imagem enviada ao tamanho padrão da marca d'água (<see cref="PlayerWatermark.StandardWidth"/>
/// × <see cref="PlayerWatermark.StandardHeight"/>), mantendo a proporção e a transparência.
/// Imagem maior é reduzida; menor fica como está, porque ampliar é o que perde qualidade.
/// </summary>
public static class WatermarkImageProcessor
{
    /// <summary>Tamanho máximo do arquivo enviado. O que fica guardado é a versão reduzida.</summary>
    public const int MaxUploadBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Maior largura ou altura do original. Um PNG pequeno pode declarar dimensões enormes;
    /// o limite vem antes da decodificação, que alocaria largura × altura × 4 bytes.
    /// </summary>
    public const int MaxSourceDimension = 4096;

    /// <summary>Leva a imagem ao tamanho padrão da marca d'água.</summary>
    public static byte[] Normalize(byte[] original) =>
        Normalize(original, PlayerWatermark.StandardWidth, PlayerWatermark.StandardHeight,
            PlayerWatermark.MinLongestSide, "The image must be at least 160 pixels on its longest side.");

    /// <summary>
    /// Leva a imagem a caber em <paramref name="maxWidth"/> × <paramref name="maxHeight"/>, com
    /// as mesmas regras da marca d'água. Serve também ao logotipo do site, que tem outro tamanho.
    /// </summary>
    /// <param name="tooSmall">Mensagem para um lado maior abaixo de <paramref name="minLongestSide"/>.</param>
    public static byte[] Normalize(byte[] original, int maxWidth, int maxHeight, int minLongestSide, string tooSmall)
    {
        ArgumentNullException.ThrowIfNull(original);

        if (original.Length > MaxUploadBytes)
            throw new ArgumentException("The image is larger than 5 MB.");

        var (largura, altura) = PlayerWatermark.ReadPngSize(original);

        if (largura > MaxSourceDimension || altura > MaxSourceDimension)
            throw new ArgumentException("The image must be at most 4096 pixels on each side.");

        if (Math.Max(largura, altura) < minLongestSide)
            throw new ArgumentException(tooSmall);

        using var decodificada = Decodificar(original);

        var escala = Math.Min(1.0, Math.Min(
            (double)maxWidth / decodificada.Width,
            (double)maxHeight / decodificada.Height));

        var alvoLargura = Math.Max(1, (int)Math.Round(decodificada.Width * escala));
        var alvoAltura = Math.Max(1, (int)Math.Round(decodificada.Height * escala));

        using var reduzida = Reduzir(decodificada, alvoLargura, alvoAltura);
        using var imagem = SKImage.FromBitmap(reduzida);
        using var png = imagem.Encode(SKEncodedImageFormat.Png, 100);

        return png.ToArray();
    }

    /// <summary>
    /// Decodifica em RGBA, com alfa pré-multiplicado e cores convertidas para sRGB — o que o
    /// navegador exibe. Metadados do arquivo original (EXIF, textos) ficam para trás.
    /// </summary>
    private static SKBitmap Decodificar(byte[] original)
    {
        using var dados = SKData.CreateCopy(original);
        using var codec = SKCodec.Create(dados);

        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png)
            throw new ArgumentException("The file is not a PNG image.");

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height,
            SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());

        var bitmap = new SKBitmap(info);
        var resultado = codec.GetPixels(info, bitmap.GetPixels());

        if (resultado is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new ArgumentException("The file is not a PNG image.");
        }

        return bitmap;
    }

    /// <summary>
    /// Reduz em etapas: metades sucessivas (cada uma equivale a uma média de 2 × 2 pixels)
    /// até ficar a menos do dobro do alvo, e o passo final com filtro cúbico de Mitchell.
    /// Reduzir muito de uma vez só com um filtro de poucos pontos pula pixels e serrilha
    /// bordas finas, que é justamente o que um logotipo tem.
    /// </summary>
    private static SKBitmap Reduzir(SKBitmap origem, int largura, int altura)
    {
        var atual = origem.Copy();

        while (atual.Width >= largura * 2 && atual.Height >= altura * 2)
        {
            var metade = atual.Resize(
                atual.Info.WithSize(atual.Width / 2, atual.Height / 2),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));

            atual.Dispose();
            atual = metade;
        }

        if (atual.Width == largura && atual.Height == altura)
            return atual;

        var final = atual.Resize(atual.Info.WithSize(largura, altura), new SKSamplingOptions(SKCubicResampler.Mitchell));
        atual.Dispose();

        return final;
    }
}
