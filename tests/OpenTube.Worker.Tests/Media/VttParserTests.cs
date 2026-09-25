using OpenTube.Worker.Media;

namespace OpenTube.Worker.Tests.Media;

public class VttParserTests
{
    private const string Legenda = """
        WEBVTT

        NOTE gerada automaticamente

        1
        00:00:01.000 --> 00:00:04.000
        Bom dia a todos.

        2
        00:00:04.500 --> 00:00:08.000
        <v Allan>Vamos falar sobre o orçamento</v>
        do próximo ano.
        """;

    [Fact]
    public void Extrai_apenas_a_fala()
    {
        var texto = VttParser.ExtractText(Legenda);

        Assert.Equal("Bom dia a todos. Vamos falar sobre o orçamento do próximo ano.", texto);
    }

    [Fact]
    public void Descarta_tempos_numeracao_e_anotacoes()
    {
        var texto = VttParser.ExtractText(Legenda);

        Assert.DoesNotContain("-->", texto);
        Assert.DoesNotContain("WEBVTT", texto);
        Assert.DoesNotContain("NOTE", texto);
        Assert.DoesNotContain("automaticamente", texto);
    }

    [Fact]
    public void Remove_marcacao_de_estilo()
    {
        var texto = VttParser.ExtractText("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<i>em itálico</i>");

        Assert.Equal("em itálico", texto);
    }

    [Fact]
    public void Descarta_blocos_de_estilo_e_regiao()
    {
        var texto = VttParser.ExtractText("WEBVTT\n\nSTYLE\n\nREGION\n\n00:00:01.000 --> 00:00:02.000\nfala");

        Assert.Equal("fala", texto);
    }

    [Fact]
    public void Lida_com_quebra_de_linha_do_windows()
    {
        var texto = VttParser.ExtractText("WEBVTT\r\n\r\n00:00:01.000 --> 00:00:02.000\r\nfala\r\n");

        Assert.Equal("fala", texto);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Conteudo_vazio_devolve_texto_vazio(string? conteudo)
    {
        Assert.Equal(string.Empty, VttParser.ExtractText(conteudo));
    }

    [Theory]
    [InlineData("WEBVTT")]
    [InlineData("  WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nfala")]
    [InlineData("﻿WEBVTT")]
    public void Reconhece_um_arquivo_de_legenda(string conteudo)
    {
        Assert.True(VttParser.IsWebVtt(conteudo));
    }

    [Theory]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\nfala")]
    [InlineData("qualquer texto")]
    [InlineData("")]
    [InlineData(null)]
    public void Recusa_o_que_nao_e_webvtt(string? conteudo)
    {
        // Um arquivo em outro formato só apareceria como falha no player de quem assiste.
        Assert.False(VttParser.IsWebVtt(conteudo));
    }
}
