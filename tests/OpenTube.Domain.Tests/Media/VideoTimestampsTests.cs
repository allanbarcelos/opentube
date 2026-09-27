// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Media;

namespace OpenTube.Domain.Tests.Media;

public class VideoTimestampsTests
{
    private static readonly TimeSpan DuasHoras = TimeSpan.FromHours(2);

    [Theory]
    [InlineData("1:05:10", 3910)]
    [InlineData("05:10", 310)]
    [InlineData("5:10", 310)]
    [InlineData("0:07", 7)]
    [InlineData("75:30", 4530)]
    [InlineData("90", 90)]
    [InlineData(" 2:00 ", 120)]
    public void Le_o_tempo_como_se_escreve(string texto, int segundos)
    {
        Assert.True(VideoTimestamps.TryParse(texto, out var tempo));
        Assert.Equal(TimeSpan.FromSeconds(segundos), tempo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("5:7")]
    [InlineData("5:60")]
    [InlineData("1:60:00")]
    [InlineData("abc")]
    [InlineData("1:05:10 e mais")]
    [InlineData("-5")]
    public void Recusa_o_que_nao_e_tempo(string texto) =>
        Assert.False(VideoTimestamps.TryParse(texto, out _));

    [Theory]
    [InlineData(3910, "1:05:10")]
    [InlineData(310, "5:10")]
    [InlineData(7, "0:07")]
    [InlineData(3600, "1:00:00")]
    public void Escreve_como_se_le(int segundos, string texto) =>
        Assert.Equal(texto, VideoTimestamps.Format(TimeSpan.FromSeconds(segundos)));

    [Fact]
    public void Separa_os_tempos_citados_na_mensagem()
    {
        var partes = VideoTimestamps.Split("Em 1:05:10 e de novo em 5:10 o áudio falha.", DuasHoras);

        Assert.Equal(["Em ", "1:05:10", " e de novo em ", "5:10", " o áudio falha."], partes.Select(p => p.Text));
        Assert.Equal([null, 3910, null, 310, null], partes.Select(p => p.Seconds));
    }

    [Fact]
    public void Tempo_alem_da_duracao_do_video_continua_texto()
    {
        var partes = VideoTimestamps.Split("Reunião às 14:30, problema em 2:15.", TimeSpan.FromMinutes(10));

        Assert.Equal(["Reunião às 14:30, problema em ", "2:15", "."], partes.Select(p => p.Text));
    }

    [Theory]
    [InlineData("versão 1:2:30")]
    [InlineData("10:30:45:12")]
    [InlineData("a1:30")]
    public void Nao_reconhece_tempo_colado_em_outros_numeros(string texto)
    {
        Assert.DoesNotContain(VideoTimestamps.Split(texto, DuasHoras), p => p.IsTimestamp);
    }

    [Fact]
    public void Sem_duracao_nada_vira_link()
    {
        var partes = VideoTimestamps.Split("em 5:10", null);

        Assert.Equal("em 5:10", Assert.Single(partes).Text);
    }
}
