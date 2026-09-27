// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Buffers.Binary;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;

namespace OpenTube.Domain.Tests.Entities;

public class PlayerWatermarkTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();

    /// <summary>
    /// Só o começo de um PNG: assinatura e cabeçalho IHDR, que é o que a entidade confere.
    /// </summary>
    private static byte[] Png(uint largura = 320, uint altura = 120, int tamanho = 64)
    {
        var bytes = new byte[tamanho];
        byte[] assinatura = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        assinatura.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), largura);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), altura);
        return bytes;
    }

    [Fact]
    public void Guarda_a_imagem_as_dimensoes_e_a_posicao()
    {
        var marca = PlayerWatermark.Define(Png(320, 120), WatermarkPosition.BottomRight, Admin, Agora);

        Assert.Equal(PlayerWatermark.SingletonId, marca.Id);
        Assert.Equal(320, marca.Width);
        Assert.Equal(120, marca.Height);
        Assert.Equal(WatermarkPosition.BottomRight, marca.Position);
        Assert.Equal(Admin, marca.UpdatedBy);
        Assert.Equal(Agora.ToUnixTimeMilliseconds(), marca.Version);
    }

    [Fact]
    public void Recusa_arquivo_que_nao_e_png_mesmo_com_o_nome_certo()
    {
        // Começo de um JPEG: o que o navegador chamar de "image/png" não importa.
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01,
                       0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

        var erro = Assert.Throws<ArgumentException>(() => PlayerWatermark.Define(jpeg, WatermarkPosition.TopLeft, Admin, Agora));
        Assert.Equal("The file is not a PNG image.", erro.Message);
    }

    [Fact]
    public void Recusa_arquivo_vazio_ou_truncado()
    {
        Assert.Throws<ArgumentException>(() => PlayerWatermark.Define([], WatermarkPosition.TopLeft, Admin, Agora));
        Assert.Throws<ArgumentException>(() => PlayerWatermark.Define(Png()[..16], WatermarkPosition.TopLeft, Admin, Agora));
    }

    [Fact]
    public void Recusa_imagem_maior_que_um_megabyte()
    {
        var erro = Assert.Throws<ArgumentException>(() =>
            PlayerWatermark.Define(Png(tamanho: PlayerWatermark.MaxBytes + 1), WatermarkPosition.TopLeft, Admin, Agora));

        Assert.Equal("The image is larger than 1 MB.", erro.Message);
    }

    [Theory]
    [InlineData(0u, 100u)]
    [InlineData(100u, 0u)]
    [InlineData(5000u, 100u)]
    [InlineData(100u, 4097u)]
    public void Recusa_dimensoes_invalidas(uint largura, uint altura)
    {
        Assert.Throws<ArgumentException>(() =>
            PlayerWatermark.Define(Png(largura, altura), WatermarkPosition.TopLeft, Admin, Agora));
    }

    [Theory]
    [InlineData(640u, 320u)]
    [InlineData(160u, 10u)]
    [InlineData(10u, 160u)]
    public void Aceita_imagem_dentro_do_tamanho_padrao(uint largura, uint altura)
    {
        var marca = PlayerWatermark.Define(Png(largura, altura), WatermarkPosition.TopLeft, Admin, Agora);

        Assert.Equal((int)largura, marca.Width);
    }

    [Theory]
    [InlineData(641u, 100u)]
    [InlineData(300u, 321u)]
    public void Recusa_guardar_imagem_acima_do_tamanho_padrao(uint largura, uint altura)
    {
        // O redimensionamento acontece antes de chegar aqui; o domínio só garante o resultado.
        var erro = Assert.Throws<ArgumentException>(() =>
            PlayerWatermark.Define(Png(largura, altura), WatermarkPosition.TopLeft, Admin, Agora));

        Assert.Equal("The image must be resized to the standard watermark size first.", erro.Message);
    }

    [Fact]
    public void Recusa_imagem_pequena_demais_para_nao_ampliar()
    {
        var erro = Assert.Throws<ArgumentException>(() =>
            PlayerWatermark.Define(Png(159, 80), WatermarkPosition.TopLeft, Admin, Agora));

        Assert.Equal("The image must be at least 160 pixels on its longest side.", erro.Message);
    }

    [Fact]
    public void Le_as_dimensoes_de_um_png_grande_sem_impor_limite()
    {
        // O original, antes de reduzido, pode ser bem maior que o tamanho padrão.
        Assert.Equal((4000, 2000), PlayerWatermark.ReadPngSize(Png(4000, 2000)));
    }

    [Fact]
    public void Recusa_posicao_inexistente()
    {
        Assert.Throws<ArgumentException>(() =>
            PlayerWatermark.Define(Png(), (WatermarkPosition)99, Admin, Agora));
    }

    [Fact]
    public void Trocar_a_imagem_ou_a_posicao_muda_a_versao()
    {
        var marca = PlayerWatermark.Define(Png(), WatermarkPosition.TopLeft, Admin, Agora);
        var versaoInicial = marca.Version;

        marca.MoveTo(WatermarkPosition.Center, Admin, Agora.AddMinutes(1));
        Assert.Equal(WatermarkPosition.Center, marca.Position);
        Assert.NotEqual(versaoInicial, marca.Version);

        var versaoPosicao = marca.Version;
        marca.Replace(Png(200, 200), WatermarkPosition.Center, Admin, Agora.AddMinutes(2));
        Assert.Equal(200, marca.Width);
        Assert.NotEqual(versaoPosicao, marca.Version);
    }

    [Fact]
    public void Troca_invalida_nao_altera_a_marca_atual()
    {
        var marca = PlayerWatermark.Define(Png(320, 120), WatermarkPosition.TopLeft, Admin, Agora);

        Assert.Throws<ArgumentException>(() => marca.Replace([1, 2, 3], WatermarkPosition.Center, Admin, Agora.AddMinutes(1)));

        Assert.Equal(320, marca.Width);
        Assert.Equal(WatermarkPosition.TopLeft, marca.Position);
        Assert.Equal(Agora.ToUnixTimeMilliseconds(), marca.Version);
    }
}
