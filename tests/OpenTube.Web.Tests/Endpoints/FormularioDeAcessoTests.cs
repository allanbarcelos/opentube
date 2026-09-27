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
    [InlineData("99999")]
    public void Prazo_em_dias_invalido_e_recusado_em_vez_de_virar_sem_prazo(string valor)
    {
        var erro = Assert.Throws<InvalidOperationException>(() => AccessEndpoints.MontarValidade("dias", valor));

        Assert.Equal("Enter the number of days, from 1 to 3650.", erro.Message);
    }

    [Fact]
    public void Campo_proprio_de_dias_tem_preferencia()
    {
        Assert.Equal(TimeSpan.FromDays(15), AccessEndpoints.MontarValidade("dias", null, dias: "15").DurationAfterFirstUse);
    }

    [Fact]
    public void Data_fixa_define_o_termino()
    {
        var validade = AccessEndpoints.MontarValidade("ate", "2099-12-31");

        Assert.NotNull(validade.ExpiresAt);
        Assert.Null(validade.DurationAfterFirstUse);
    }

    [Fact]
    public void A_data_vale_ate_o_fim_do_dia_escolhido()
    {
        var validade = AccessEndpoints.MontarValidade("ate", null, data: "2099-12-31");

        var fim = validade.ExpiresAt!.Value.ToLocalTime();
        Assert.Equal(new DateTime(2100, 1, 1), fim.DateTime);
    }

    [Theory]
    [InlineData("trinta e um")]
    [InlineData("")]
    [InlineData("2001-01-01")]
    public void Data_invalida_ou_passada_e_recusada(string valor)
    {
        Assert.Throws<InvalidOperationException>(() => AccessEndpoints.MontarValidade("ate", null, data: valor));
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
