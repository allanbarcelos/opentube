// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Web.Endpoints;

namespace OpenTube.Web.Tests.Endpoints;

/// <summary>Conversão dos campos do formulário de concessão, que não depende de servidor.</summary>
public class FormularioDeAcessoTests
{
    [Fact]
    public void Sem_prazo_e_o_padrao()
    {
        Assert.Equal("no end date", AccessEndpoints.MontarValidade("sempre", null).Describe());
        Assert.Equal("no end date", AccessEndpoints.MontarValidade(null, "30").Describe());
        Assert.Equal("no end date", AccessEndpoints.MontarValidade("qualquer-coisa", "30").Describe());
    }

    [Fact]
    public void Prazo_em_dias_conta_do_primeiro_acesso()
    {
        var validade = AccessEndpoints.MontarValidade("dias", "30");

        Assert.Equal(TimeSpan.FromDays(30), validade.DurationAfterFirstUse);
        Assert.Null(validade.ExpiresAt);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("")]
    public void Prazo_em_dias_invalido_vira_sem_prazo(string valor)
    {
        Assert.Equal("no end date", AccessEndpoints.MontarValidade("dias", valor).Describe());
    }

    [Fact]
    public void Data_fixa_define_o_termino()
    {
        var validade = AccessEndpoints.MontarValidade("ate", "2026-12-31");

        Assert.NotNull(validade.ExpiresAt);
        Assert.Null(validade.DurationAfterFirstUse);
    }

    [Fact]
    public void Data_invalida_vira_sem_prazo()
    {
        Assert.Equal("no end date", AccessEndpoints.MontarValidade("ate", "trinta e um").Describe());
    }

    [Theory]
    [InlineData("a@b.com, c@d.com", 2)]
    [InlineData("a@b.com; c@d.com", 2)]
    [InlineData("a@b.com\nc@d.com", 2)]
    [InlineData("a@b.com c@d.com", 2)]
    [InlineData("  a@b.com  ", 1)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void Separa_os_enderecos_por_qualquer_delimitador_comum(string? texto, int esperado)
    {
        Assert.Equal(esperado, AccessEndpoints.SepararEmails(texto).Count());
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("0", null)]
    [InlineData("-3", null)]
    [InlineData("muitos", null)]
    public void Le_o_limite_de_visualizacoes(string? valor, int? esperado)
    {
        Assert.Equal(esperado, AccessEndpoints.LimiteDeVisualizacoes(valor));
    }

    [Theory]
    [InlineData("treinamento, segurança", 2)]
    [InlineData("uma-etiqueta", 1)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void Separa_as_etiquetas_do_video(string? texto, int esperado)
    {
        Assert.Equal(esperado, AdminEndpoints.SepararEtiquetas(texto).Count());
    }
}
