// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Infrastructure.Security;

namespace OpenTube.Infrastructure.Tests.Security;

public class LinkSealerTests
{
    private const string Segredo = "segredo-do-servidor";

    [Fact]
    public void O_token_cifrado_abre_de_volta_com_o_mesmo_segredo()
    {
        var cifrado = LinkSealer.Seal("token-do-link", Segredo);

        Assert.DoesNotContain("token-do-link", cifrado);
        Assert.Equal("token-do-link", LinkSealer.Open(cifrado, Segredo));
    }

    [Fact]
    public void Cifrar_duas_vezes_da_resultados_diferentes()
    {
        Assert.NotEqual(LinkSealer.Seal("token-do-link", Segredo), LinkSealer.Seal("token-do-link", Segredo));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("não é base64")]
    [InlineData("AAAA")]
    public void Valor_invalido_nao_abre(string? cifrado)
    {
        Assert.Null(LinkSealer.Open(cifrado, Segredo));
    }

    [Fact]
    public void Com_outro_segredo_nao_abre()
    {
        var cifrado = LinkSealer.Seal("token-do-link", Segredo);

        Assert.Null(LinkSealer.Open(cifrado, "outro-segredo"));
    }

    [Fact]
    public void Adulterado_nao_abre()
    {
        var dados = Convert.FromBase64String(LinkSealer.Seal("token-do-link", Segredo));
        dados[^1] ^= 1;

        Assert.Null(LinkSealer.Open(Convert.ToBase64String(dados), Segredo));
    }
}
