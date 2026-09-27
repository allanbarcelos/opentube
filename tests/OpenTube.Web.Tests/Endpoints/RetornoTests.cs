// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Web.Endpoints;

namespace OpenTube.Web.Tests.Endpoints;

/// <summary>O endereço de volta de um formulário nunca leva para fora do site.</summary>
public class RetornoTests
{
    [Theory]
    [InlineData("/watch/reuniao", "ok=1", "/watch/reuniao?ok=1")]
    [InlineData("/watch/reuniao?t=10", "ok=1", "/watch/reuniao?t=10&ok=1")]
    [InlineData("/watch/reuniao#avaliacao", "ok=1", "/watch/reuniao?ok=1#avaliacao")]
    [InlineData("/", "ok=1", "/?ok=1")]
    public void Caminho_do_proprio_site_e_mantido(string destino, string parametro, string esperado) =>
        Assert.Equal(esperado, Retorno.Para(destino, parametro));

    [Theory]
    [InlineData("https://exemplo-malicioso.com")]
    [InlineData("//exemplo-malicioso.com")]
    [InlineData("/\\exemplo-malicioso.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData(null)]
    public void Endereco_de_fora_volta_para_a_raiz(string? destino) =>
        Assert.Equal("/?ok=1", Retorno.Para(destino, "ok=1"));
}
