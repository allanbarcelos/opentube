// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Buffers.Binary;
using OpenTube.Domain.Entities;

namespace OpenTube.Domain.Tests.Entities;

public class SiteBrandingTests
{
    private static readonly Guid Admin = Guid.CreateVersion7();
    private static readonly DateTimeOffset Agora = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Só o começo de um PNG: assinatura e cabeçalho IHDR, que é o que a entidade confere.</summary>
    private static byte[] Png(int largura, int altura, int tamanho = 64)
    {
        var bytes = new byte[tamanho];
        byte[] assinatura = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        assinatura.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)largura);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)altura);
        return bytes;
    }

    [Fact]
    public void Guarda_o_nome_sem_os_espacos_das_pontas()
    {
        var personalizacao = SiteBranding.Define("  Vídeos da Acme  ", false, true, Admin, Agora);

        Assert.Equal("Vídeos da Acme", personalizacao.Name);
        Assert.False(personalizacao.ShowPoweredBy);
        Assert.True(personalizacao.ShowRepositoryLink);
        Assert.Null(personalizacao.Logo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Linha\nquebrada")]
    public void Recusa_nome_vazio_ou_com_quebra_de_linha(string nome)
    {
        Assert.Throws<ArgumentException>(() => SiteBranding.Define(nome, true, true, Admin, Agora));
    }

    [Fact]
    public void Recusa_nome_longo_demais()
    {
        var nome = new string('a', SiteBranding.MaxNameLength + 1);

        Assert.Throws<ArgumentException>(() => SiteBranding.Define(nome, true, true, Admin, Agora));
        Assert.Equal(nome[..^1], SiteBranding.Define(nome[..^1], true, true, Admin, Agora).Name);
    }

    [Fact]
    public void Aceita_logotipo_no_tamanho_padrao_e_o_remove()
    {
        var personalizacao = SiteBranding.Define("Acme", true, true, Admin, Agora);

        personalizacao.SetLogo(Png(240, 64), Admin, Agora.AddMinutes(1));

        Assert.Equal(240, personalizacao.LogoWidth);
        Assert.Equal(64, personalizacao.LogoHeight);
        Assert.Equal(Agora.AddMinutes(1).ToUnixTimeMilliseconds(), personalizacao.Version);

        personalizacao.RemoveLogo(Admin, Agora.AddMinutes(2));

        Assert.Null(personalizacao.Logo);
        Assert.Null(personalizacao.LogoWidth);
    }

    [Theory]
    [InlineData(SiteBranding.LogoStandardWidth + 1, 64)]
    [InlineData(64, SiteBranding.LogoStandardHeight + 1)]
    [InlineData(20, 20)]
    public void Recusa_logotipo_fora_do_tamanho_padrao(int largura, int altura)
    {
        var personalizacao = SiteBranding.Define("Acme", true, true, Admin, Agora);

        Assert.Throws<ArgumentException>(() => personalizacao.SetLogo(Png(largura, altura), Admin, Agora));
    }

    [Fact]
    public void Recusa_logotipo_maior_que_o_limite()
    {
        var personalizacao = SiteBranding.Define("Acme", true, true, Admin, Agora);

        Assert.Throws<ArgumentException>(() =>
            personalizacao.SetLogo(Png(240, 64, SiteBranding.LogoMaxBytes + 1), Admin, Agora));
    }

    [Fact]
    public void Recusa_arquivo_que_nao_e_png()
    {
        var personalizacao = SiteBranding.Define("Acme", true, true, Admin, Agora);

        Assert.Throws<ArgumentException>(() => personalizacao.SetLogo("GIF89a nada de png"u8.ToArray(), Admin, Agora));
    }
}
