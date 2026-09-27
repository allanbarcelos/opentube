using OpenTube.Domain.Captions;

namespace OpenTube.Domain.Tests.Captions;

public class CaptionDocumentTests
{
    private static TimeSpan T(double segundos) => TimeSpan.FromSeconds(segundos);

    [Fact]
    public void Le_webvtt_com_cabecalho_identificadores_comentarios_e_ajustes()
    {
        const string vtt = """
            WEBVTT
            Kind: captions

            NOTE isto é um comentário

            1
            00:00:01.000 --> 00:00:03.500 align:start position:10%
            Bom dia a todos.
            <i>Sejam bem-vindos.</i>

            00:04.250 --> 00:06.000
            Vamos começar.
            """;

        var legenda = CaptionDocument.Parse(vtt);

        Assert.Equal(2, legenda.Cues.Count);
        Assert.Equal(new CaptionCue(T(1), T(3.5), "Bom dia a todos.\n<i>Sejam bem-vindos.</i>", "align:start position:10%"), legenda.Cues[0]);
        Assert.Equal(new CaptionCue(T(4.25), T(6), "Vamos começar."), legenda.Cues[1]);
    }

    [Fact]
    public void Le_srt_e_escreve_webvtt()
    {
        const string srt = "1\r\n00:00:01,000 --> 00:00:02,500\r\nPrimeira fala\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nSegunda\r\nem duas linhas\r\n";

        var legenda = CaptionDocument.Parse(srt);

        Assert.Equal("""
            WEBVTT

            00:00:01.000 --> 00:00:02.500
            Primeira fala

            00:00:03.000 --> 00:00:04.000
            Segunda
            em duas linhas

            """.Replace("\r\n", "\n"), legenda.ToWebVtt());
    }

    [Fact]
    public void Ida_e_volta_preserva_os_trechos()
    {
        var original = CaptionDocument.FromCues([
            new CaptionCue(T(0.5), T(2), "Um", "line:90%"),
            new CaptionCue(T(3725.125), T(3727), "Depois de uma hora")
        ]);

        var relida = CaptionDocument.Parse(original.ToWebVtt());

        Assert.Equal(original.Cues, relida.Cues);
        Assert.Contains("01:02:05.125 --> 01:02:07.000", original.ToWebVtt());
    }

    [Fact]
    public void Aceita_marca_de_ordem_de_bytes_do_windows()
    {
        var legenda = CaptionDocument.Parse("﻿WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nOi\n");

        Assert.Single(legenda.Cues);
    }

    [Fact]
    public void Ordena_os_trechos_pela_entrada()
    {
        var legenda = CaptionDocument.FromCues([
            new CaptionCue(T(5), T(6), "Depois"),
            new CaptionCue(T(1), T(2), "Antes")
        ]);

        Assert.Equal(["Antes", "Depois"], legenda.Cues.Select(c => c.Text));
    }

    [Fact]
    public void Limpa_linhas_em_branco_e_espacos_do_texto()
    {
        var legenda = CaptionDocument.FromCues([new CaptionCue(T(1), T(2), "  Primeira  \n\n  segunda \n")]);

        Assert.Equal("Primeira\nsegunda", legenda.Cues[0].Text);
    }

    [Fact]
    public void Texto_para_a_busca_sai_sem_tempos_nem_marcacao()
    {
        var legenda = CaptionDocument.FromCues([
            new CaptionCue(T(1), T(2), "<i>Bom</i> dia"),
            new CaptionCue(T(3), T(4), "a todos\nde novo")
        ]);

        Assert.Equal("Bom dia a todos de novo", legenda.PlainText);
    }

    [Theory]
    [InlineData("00:00:01.000", 1.0)]
    [InlineData("01:02.5", 62.5)]
    [InlineData("1:00:00,250", 3600.25)]
    [InlineData("100:00:00.000", 360000.0)]
    public void Le_os_formatos_de_tempo(string texto, double segundos)
    {
        Assert.True(CaptionDocument.TryParseTime(texto, out var tempo));
        Assert.Equal(T(segundos), tempo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.5")]
    [InlineData("00:60:00.000")]
    [InlineData("00:00:61.000")]
    [InlineData("aa:bb:cc.ddd")]
    public void Recusa_tempos_invalidos(string texto)
    {
        Assert.False(CaptionDocument.TryParseTime(texto, out _));
    }

    [Fact]
    public void Recusa_trecho_que_termina_antes_de_comecar()
    {
        var erro = Assert.Throws<CaptionFormatException>(() =>
            CaptionDocument.FromCues([new CaptionCue(T(1), T(2), "ok"), new CaptionCue(T(5), T(4), "errado")]));

        Assert.Equal("Cue {0}: the end time must come after the start time.", erro.Key);
        Assert.Equal([2], erro.Args);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("tem --> seta")]
    public void Recusa_texto_vazio_ou_com_seta(string texto)
    {
        Assert.Throws<CaptionFormatException>(() => CaptionDocument.FromCues([new CaptionCue(T(1), T(2), texto)]));
    }

    [Fact]
    public void Recusa_texto_longo_demais()
    {
        Assert.Throws<CaptionFormatException>(() =>
            CaptionDocument.FromCues([new CaptionCue(T(1), T(2), new string('a', CaptionDocument.MaxCueLength + 1))]));
    }

    [Fact]
    public void Aponta_a_linha_do_tempo_mal_escrito()
    {
        var erro = Assert.Throws<CaptionFormatException>(() =>
            CaptionDocument.Parse("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nOk\n\n00:00:03 --> 00:00:04.000\nQuebrado\n"));

        Assert.Equal("Line {0}: expected a time like 00:00:01.000 --> 00:00:03.000.", erro.Key);
        Assert.Equal([6], erro.Args);
    }

    [Theory]
    [InlineData("")]
    [InlineData("isto não é legenda")]
    [InlineData("<html></html>")]
    public void Recusa_o_que_nao_e_webvtt_nem_srt(string conteudo)
    {
        Assert.Throws<CaptionFormatException>(() => CaptionDocument.Parse(conteudo));
    }

    [Fact]
    public void Legenda_sem_trechos_e_valida()
    {
        Assert.Empty(CaptionDocument.Parse("WEBVTT\n").Cues);
        Assert.Equal("WEBVTT\n", CaptionDocument.FromCues([]).ToWebVtt());
    }
}
