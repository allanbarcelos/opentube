// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Infrastructure.Storage;

namespace OpenTube.Infrastructure.Tests.Storage;

public class MediaTypesTests
{
    [Theory]
    [InlineData("reuniao.mp4", "video/mp4")]
    [InlineData("reuniao.MKV", null)]
    [InlineData("gravacao.mov", "")]
    [InlineData("sem-extensao", "video/quicktime")]
    [InlineData("captura.m2ts", null)]
    public void Reconhece_o_que_parece_video(string nome, string? tipo)
    {
        Assert.True(MediaTypes.LooksLikeVideo(nome, tipo));
    }

    [Theory]
    [InlineData("planilha.xlsx", "application/vnd.ms-excel")]
    [InlineData("foto.jpg", "image/jpeg")]
    [InlineData("documento.pdf", null)]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void Barra_o_que_claramente_nao_e_video(string? nome, string? tipo)
    {
        Assert.False(MediaTypes.LooksLikeVideo(nome, tipo));
    }

    [Theory]
    [InlineData("master.m3u8", MediaTypes.HlsPlaylist)]
    [InlineData("seg-00001.m4s", MediaTypes.HlsSegment)]
    [InlineData("init.mp4", MediaTypes.Mp4)]
    [InlineData("pt-br.vtt", MediaTypes.WebVtt)]
    [InlineData("thumb.jpg", MediaTypes.Jpeg)]
    [InlineData("qualquer.coisa", "application/octet-stream")]
    public void Define_o_tipo_dos_arquivos_gerados(string arquivo, string esperado)
    {
        Assert.Equal(esperado, MediaTypes.ForOutput(arquivo));
    }
}
