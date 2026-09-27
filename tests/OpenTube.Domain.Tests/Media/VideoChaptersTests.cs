// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Media;

namespace OpenTube.Domain.Tests.Media;

public class VideoChaptersTests
{
    private static readonly TimeSpan DezMinutos = TimeSpan.FromMinutes(10);

    [Fact]
    public void Le_ordena_e_ignora_linhas_em_branco()
    {
        var capitulos = VideoChapters.Parse(
        [
            ("5:00", " Perguntas "),
            ("", ""),
            ("0:00", "Abertura"),
            ("1:30", "Resultados")
        ], DezMinutos);

        Assert.Equal(
            [new Chapter(0, "Abertura"), new Chapter(90, "Resultados"), new Chapter(300, "Perguntas")],
            capitulos);
    }

    [Fact]
    public void Nada_preenchido_e_sumario_vazio() =>
        Assert.Empty(VideoChapters.Parse([("", ""), (null, null)], DezMinutos));

    [Theory]
    [InlineData("abc", "Abertura", "Chapter 1: write the start like 0:00, 5:10 or 1:05:10.")]
    [InlineData("0:00", "  ", "Chapter 1: the title is empty.")]
    [InlineData("10:00", "Fim", "Chapter 1: 10:00 is past the end of the video (10:00).")]
    [InlineData("", "Sem tempo", "Chapter 1: write the start like 0:00, 5:10 or 1:05:10.")]
    public void Linha_errada_e_recusada_com_o_motivo(string inicio, string titulo, string mensagem)
    {
        var erro = Assert.Throws<ChapterException>(() => VideoChapters.Parse([(inicio, titulo)], DezMinutos));

        Assert.Equal(mensagem, erro.Message);
    }

    [Fact]
    public void Titulo_longo_demais_e_recusado() =>
        Assert.Throws<ChapterException>(() =>
            VideoChapters.Parse([("0:00", new string('a', VideoChapters.MaxTitleLength + 1))], DezMinutos));

    [Fact]
    public void Dois_capitulos_no_mesmo_instante_sao_recusados()
    {
        var erro = Assert.Throws<ChapterException>(() =>
            VideoChapters.Parse([("1:00", "A"), ("0:00", "B"), ("60", "C")], DezMinutos));

        Assert.Equal("Two chapters start at 1:00.", erro.Message);
    }

    [Fact]
    public void Cada_capitulo_termina_no_inicio_do_seguinte_e_o_ultimo_no_fim()
    {
        var capitulos = VideoChapters.Parse([("0:00", "A"), ("2:00", "B")], TimeSpan.FromSeconds(300.4));

        Assert.Equal(120, VideoChapters.EndOf(capitulos, 0, TimeSpan.FromSeconds(300.4)));
        Assert.Equal(301, VideoChapters.EndOf(capitulos, 1, TimeSpan.FromSeconds(300.4)));
    }
}
