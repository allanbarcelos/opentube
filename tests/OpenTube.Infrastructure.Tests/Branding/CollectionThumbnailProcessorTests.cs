// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Buffers.Binary;
using OpenTube.Infrastructure.Branding;
using OpenTube.TestSupport;
using SkiaSharp;

namespace OpenTube.Infrastructure.Tests.Branding;

public class CollectionThumbnailProcessorTests
{
    private static SKBitmap Ler(byte[] jpeg) => SKBitmap.Decode(jpeg)
        ?? throw new InvalidOperationException("O resultado não é uma imagem legível.");

    [Theory]
    [InlineData(2000, 1000, 1280, 640)]
    [InlineData(3000, 1000, 1280, 427)]
    [InlineData(500, 2000, 180, 720)]
    [InlineData(2000, 2000, 720, 720)]
    public void Reduz_para_caber_em_1280_por_720(int largura, int altura, int esperadaLargura, int esperadaAltura)
    {
        using var imagem = Ler(CollectionThumbnailProcessor.Normalize(PngDeTeste.Criar(largura, altura)));

        Assert.Equal((esperadaLargura, esperadaAltura), (imagem.Width, imagem.Height));
    }

    [Theory]
    [InlineData(400, 200)]
    [InlineData(1280, 720)]
    [InlineData(160, 90)]
    public void Imagem_que_ja_cabe_nao_e_ampliada(int largura, int altura)
    {
        using var imagem = Ler(CollectionThumbnailProcessor.Normalize(PngDeTeste.Criar(largura, altura)));

        Assert.Equal((largura, altura), (imagem.Width, imagem.Height));
    }

    [Fact]
    public void Sai_como_jpeg_sem_transparencia()
    {
        var png = PngDeTeste.Criar(200, 160, (_, _) => (255, 0, 0, 0));

        var jpeg = CollectionThumbnailProcessor.Normalize(png);

        Assert.Equal([0xFF, 0xD8, 0xFF], jpeg[..3]);

        using var imagem = Ler(jpeg);
        var pixel = imagem.GetPixel(10, 10);
        Assert.True(pixel.Red > 250 && pixel.Green > 250 && pixel.Blue > 250);
    }

    [Fact]
    public void Cor_opaca_permanece()
    {
        using var imagem = Ler(CollectionThumbnailProcessor.Normalize(
            PngDeTeste.Criar(200, 160, (_, _) => (220, 10, 10, 255))));

        var pixel = imagem.GetPixel(10, 10);
        Assert.True(pixel.Red > 180 && pixel.Green < 40 && pixel.Blue < 40);
    }

    [Fact]
    public void Aceita_jpeg()
    {
        using var bitmap = new SKBitmap(320, 180);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(SKColors.Navy);

        using var imagem = SKImage.FromBitmap(bitmap);
        using var dados = imagem.Encode(SKEncodedImageFormat.Jpeg, 90);

        using var resultado = Ler(CollectionThumbnailProcessor.Normalize(dados.ToArray()));

        Assert.Equal((320, 180), (resultado.Width, resultado.Height));
    }

    [Fact]
    public void Foto_de_lado_e_endireitada()
    {
        using var bitmap = new SKBitmap(320, 180);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(SKColors.Navy);

        using var imagem = SKImage.FromBitmap(bitmap);
        using var dados = imagem.Encode(SKEncodedImageFormat.Jpeg, 90);
        var deLado = ComOrientacao(dados.ToArray(), 6);

        using var resultado = Ler(CollectionThumbnailProcessor.Normalize(deLado));

        Assert.Equal((180, 320), (resultado.Width, resultado.Height));
    }

    [Fact]
    public void Recusa_imagem_pequena_demais()
    {
        var erro = Assert.Throws<ArgumentException>(() =>
            CollectionThumbnailProcessor.Normalize(PngDeTeste.Criar(159, 80)));

        Assert.Equal("The image must be at least 160 pixels on its longest side.", erro.Message);
    }

    [Fact]
    public void Recusa_dimensao_enorme_antes_de_alocar_os_pixels()
    {
        var erro = Assert.Throws<ArgumentException>(() =>
            CollectionThumbnailProcessor.Normalize(PngDeTeste.Criar(4097, 200)));

        Assert.Equal("The image must be at most 4096 pixels on each side.", erro.Message);
    }

    [Fact]
    public void Recusa_o_que_nao_e_imagem()
    {
        var erro = Assert.Throws<ArgumentException>(() =>
            CollectionThumbnailProcessor.Normalize("não é uma imagem"u8.ToArray()));

        Assert.Equal("The file is not a JPEG, PNG or WebP image.", erro.Message);
    }

    [Fact]
    public void Recusa_arquivo_vazio_e_acima_de_5_mb()
    {
        Assert.Equal(
            "Choose an image.",
            Assert.Throws<ArgumentException>(() => CollectionThumbnailProcessor.Normalize([])).Message);

        var grande = new byte[CollectionThumbnailProcessor.MaxUploadBytes + 1];
        Assert.Equal(
            "The image is larger than 5 MB.",
            Assert.Throws<ArgumentException>(() => CollectionThumbnailProcessor.Normalize(grande)).Message);
    }

    /// <summary>Insere um EXIF mínimo com a orientação pedida logo depois do início do JPEG.</summary>
    private static byte[] ComOrientacao(byte[] jpeg, ushort orientacao)
    {
        var exif = Exif(orientacao);
        using var saida = new MemoryStream();
        saida.Write(jpeg, 0, 2);
        saida.Write([0xFF, 0xE1]);

        Span<byte> tamanho = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(tamanho, (ushort)(exif.Length + 2));
        saida.Write(tamanho);
        saida.Write(exif);
        saida.Write(jpeg, 2, jpeg.Length - 2);

        return saida.ToArray();
    }

    private static byte[] Exif(ushort orientacao)
    {
        using var saida = new MemoryStream();
        saida.Write("Exif\0\0"u8);
        saida.Write([0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, 0x00, 0x01]);
        saida.Write([0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01]);
        saida.Write([(byte)(orientacao >> 8), (byte)orientacao, 0x00, 0x00]);
        saida.Write([0x00, 0x00, 0x00, 0x00]);

        return saida.ToArray();
    }
}
