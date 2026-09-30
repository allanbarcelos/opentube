// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using SkiaSharp;

namespace OpenTube.Infrastructure.Branding;

/// <summary>
/// Leva a imagem enviada para a capa de uma coleção ao tamanho que o cartão usa: cabe em
/// 1280 × 720, sem ampliar, e sai como JPEG sem os metadados do arquivo original.
/// </summary>
public static class CollectionThumbnailProcessor
{
    /// <summary>Tamanho máximo do arquivo enviado. O que fica guardado é a versão reduzida.</summary>
    public const int MaxUploadBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Maior largura ou altura do original. O limite vem antes da decodificação, que alocaria
    /// largura × altura × 4 bytes.
    /// </summary>
    public const int MaxSourceDimension = 4096;

    /// <summary>Menor lado maior aceito. Abaixo disso a capa ficaria borrada no cartão.</summary>
    public const int MinLongestSide = 160;

    /// <summary>A imagem guardada cabe neste retângulo, mantendo a proporção.</summary>
    public const int MaxWidth = 1280;

    public const int MaxHeight = 720;

    private const int QualidadeJpeg = 85;

    public static byte[] Normalize(byte[] original)
    {
        ArgumentNullException.ThrowIfNull(original);

        if (original.Length == 0)
            throw new ArgumentException("Choose an image.");

        if (original.Length > MaxUploadBytes)
            throw new ArgumentException("The image is larger than 5 MB.");

        using var dados = SKData.CreateCopy(original);
        using var codec = SKCodec.Create(dados)
            ?? throw new ArgumentException("The file is not a JPEG, PNG or WebP image.");

        if (codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp))
            throw new ArgumentException("The file is not a JPEG, PNG or WebP image.");

        if (codec.Info.Width > MaxSourceDimension || codec.Info.Height > MaxSourceDimension)
            throw new ArgumentException("The image must be at most 4096 pixels on each side.");

        if (Math.Max(codec.Info.Width, codec.Info.Height) < MinLongestSide)
            throw new ArgumentException("The image must be at least 160 pixels on its longest side.");

        using var decodificada = Decodificar(codec);
        var (largura, altura) = Caber(decodificada.Width, decodificada.Height);

        using var ajustada = largura == decodificada.Width && altura == decodificada.Height
            ? decodificada.Copy()
            : Reduzir(decodificada, largura, altura);

        return CodificarJpeg(ajustada);
    }

    /// <summary>Escala para caber no retângulo padrão, sem passar de 1: imagem menor fica como está.</summary>
    private static (int Largura, int Altura) Caber(int largura, int altura)
    {
        var escala = Math.Min(1.0, Math.Min((double)MaxWidth / largura, (double)MaxHeight / altura));

        return (
            Math.Max(1, (int)Math.Round(largura * escala)),
            Math.Max(1, (int)Math.Round(altura * escala)));
    }

    private static SKBitmap Decodificar(SKCodec codec)
    {
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height,
            SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());

        var bitmap = new SKBitmap(info);
        var resultado = codec.GetPixels(info, bitmap.GetPixels());

        if (resultado is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new ArgumentException("The file is not a JPEG, PNG or WebP image.");
        }

        return AplicarOrientacao(bitmap, codec.EncodedOrigin);
    }

    /// <summary>
    /// A orientação do EXIF não entra nos pixels. Sem isto, uma foto de celular sai de lado.
    /// O arquivo de saída não leva o EXIF, então a rotação precisa acontecer aqui.
    /// </summary>
    private static SKBitmap AplicarOrientacao(SKBitmap bitmap, SKEncodedOrigin origem)
    {
        if (origem is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
            return bitmap;

        var trocaLado = origem is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        var destino = new SKBitmap(bitmap.Info.WithSize(
            trocaLado ? bitmap.Height : bitmap.Width,
            trocaLado ? bitmap.Width : bitmap.Height));

        using (var canvas = new SKCanvas(destino))
        {
            switch (origem)
            {
                case SKEncodedOrigin.TopRight:
                    canvas.Translate(bitmap.Width, 0);
                    canvas.Scale(-1, 1);
                    break;
                case SKEncodedOrigin.BottomRight:
                    canvas.Translate(bitmap.Width, bitmap.Height);
                    canvas.RotateDegrees(180);
                    break;
                case SKEncodedOrigin.BottomLeft:
                    canvas.Translate(0, bitmap.Height);
                    canvas.Scale(1, -1);
                    break;
                case SKEncodedOrigin.LeftTop:
                    canvas.Translate(bitmap.Height, 0);
                    canvas.RotateDegrees(90);
                    canvas.Scale(1, -1);
                    break;
                case SKEncodedOrigin.RightTop:
                    canvas.Translate(bitmap.Height, 0);
                    canvas.RotateDegrees(90);
                    break;
                case SKEncodedOrigin.RightBottom:
                    canvas.Translate(0, bitmap.Width);
                    canvas.RotateDegrees(-90);
                    canvas.Scale(1, -1);
                    break;
                case SKEncodedOrigin.LeftBottom:
                    canvas.Translate(0, bitmap.Width);
                    canvas.RotateDegrees(-90);
                    break;
                default:
                    destino.Dispose();
                    bitmap.Dispose();
                    throw new ArgumentException("The file is not a JPEG, PNG or WebP image.");
            }

            canvas.DrawBitmap(bitmap, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }

        bitmap.Dispose();
        return destino;
    }

    /// <summary>
    /// Reduz em etapas: metades sucessivas até ficar a menos do dobro do alvo, e o passo
    /// final com filtro cúbico de Mitchell. Reduzir muito de uma vez só serrilha a imagem.
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

    /// <summary>
    /// JPEG não tem transparência. O que era transparente assenta sobre branco, e o que fica
    /// guardado não carrega EXIF nem outros metadados do arquivo enviado.
    /// </summary>
    private static byte[] CodificarJpeg(SKBitmap origem)
    {
        var info = new SKImageInfo(origem.Width, origem.Height,
            SKColorType.Rgba8888, SKAlphaType.Opaque, SKColorSpace.CreateSrgb());

        using var superficie = SKSurface.Create(info);
        var canvas = superficie.Canvas;
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(origem, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        canvas.Flush();

        using var imagem = superficie.Snapshot();
        using var jpeg = imagem.Encode(SKEncodedImageFormat.Jpeg, QualidadeJpeg)
            ?? throw new InvalidOperationException("The file is not a JPEG, PNG or WebP image.");

        return jpeg.ToArray();
    }
}
