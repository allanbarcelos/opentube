// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Access;

namespace OpenTube.Domain.Tests.Access;

public class PublicEmailProvidersTests
{
    [Theory]
    [InlineData("gmail.com")]
    [InlineData("GMAIL.COM")]
    [InlineData("gmail.com.")]
    [InlineData("outlook.com")]
    [InlineData("hotmail.com")]
    [InlineData("hotmail.com.br")]
    [InlineData("outlook.fr")]
    [InlineData("yahoo.co.jp")]
    [InlineData("yahoo.com.br")]
    [InlineData("live.co.uk")]
    [InlineData("icloud.com")]
    [InlineData("proton.me")]
    [InlineData("uol.com.br")]
    [InlineData("orange.fr")]
    public void Provedor_aberto_ao_publico(string dominio)
    {
        Assert.True(PublicEmailProviders.IsPublic(dominio));
    }

    [Theory]
    [InlineData("barcelos.dev")]
    [InlineData("empresa.com.br")]
    [InlineData("live.empresa.com")]
    [InlineData("gmail.empresa.com")]
    [InlineData("outlook-consultoria.com")]
    [InlineData("")]
    public void Dominio_de_organizacao_nao_e_provedor_publico(string dominio)
    {
        Assert.False(PublicEmailProviders.IsPublic(dominio));
    }
}
