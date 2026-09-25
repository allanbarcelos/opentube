using OpenTube.Worker.Media;

namespace OpenTube.Worker.Tests.Media;

public class FfprobeOutputParserTests
{
    private const string VideoComAudio = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
              "avg_frame_rate": "30000/1001", "duration": "600.5" },
            { "codec_type": "audio", "codec_name": "aac" }
          ],
          "format": { "duration": "600.533333" }
        }
        """;

    [Fact]
    public void Le_as_caracteristicas_do_arquivo()
    {
        var info = FfprobeOutputParser.Parse(VideoComAudio);

        Assert.Equal(1920, info.Width);
        Assert.Equal(1080, info.Height);
        Assert.Equal(600.533333, info.DurationSeconds, 3);
        Assert.Equal(29.97, info.FrameRate, 2);
        Assert.True(info.HasAudio);
        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal("aac", info.AudioCodec);
        Assert.False(info.IsPortrait);
    }

    [Fact]
    public void Reconhece_video_sem_audio()
    {
        var json = """
            {
              "streams": [
                { "codec_type": "video", "codec_name": "h264", "width": 640, "height": 480, "avg_frame_rate": "25/1" }
              ],
              "format": { "duration": "12" }
            }
            """;

        var info = FfprobeOutputParser.Parse(json);

        Assert.False(info.HasAudio);
        Assert.Null(info.AudioCodec);
        Assert.Equal(25, info.FrameRate);
    }

    [Fact]
    public void Reconhece_video_em_pe()
    {
        var json = """
            {
              "streams": [ { "codec_type": "video", "width": 1080, "height": 1920, "avg_frame_rate": "30/1" } ],
              "format": { "duration": "30" }
            }
            """;

        Assert.True(FfprobeOutputParser.Parse(json).IsPortrait);
    }

    [Fact]
    public void Ignora_capa_embutida_e_usa_o_video_de_verdade()
    {
        var json = """
            {
              "streams": [
                { "codec_type": "video", "codec_name": "mjpeg", "width": 600, "height": 600,
                  "disposition": { "attached_pic": 1 } },
                { "codec_type": "video", "codec_name": "h264", "width": 1280, "height": 720, "avg_frame_rate": "30/1" }
              ],
              "format": { "duration": "45" }
            }
            """;

        var info = FfprobeOutputParser.Parse(json);

        Assert.Equal(1280, info.Width);
        Assert.Equal("h264", info.VideoCodec);
    }

    [Fact]
    public void Cai_para_a_duracao_do_fluxo_quando_o_formato_nao_informa()
    {
        var json = """
            {
              "streams": [ { "codec_type": "video", "width": 640, "height": 360, "duration": "42.5", "avg_frame_rate": "30/1" } ],
              "format": { }
            }
            """;

        Assert.Equal(42.5, FfprobeOutputParser.Parse(json).DurationSeconds);
    }

    [Fact]
    public void Recusa_arquivo_sem_fluxo_de_video()
    {
        var json = """
            { "streams": [ { "codec_type": "audio", "codec_name": "mp3" } ], "format": { "duration": "120" } }
            """;

        var erro = Assert.Throws<InvalidOperationException>(() => FfprobeOutputParser.Parse(json));

        Assert.Contains("não tem fluxo de vídeo", erro.Message);
    }

    [Fact]
    public void Recusa_video_sem_dimensoes()
    {
        var json = """
            { "streams": [ { "codec_type": "video", "codec_name": "h264" } ], "format": { "duration": "10" } }
            """;

        Assert.Throws<InvalidOperationException>(() => FfprobeOutputParser.Parse(json));
    }

    [Fact]
    public void Recusa_saida_sem_a_lista_de_fluxos()
    {
        Assert.Throws<InvalidOperationException>(() => FfprobeOutputParser.Parse("{}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Recusa_saida_vazia(string json)
    {
        Assert.ThrowsAny<ArgumentException>(() => FfprobeOutputParser.Parse(json));
    }

    [Theory]
    [InlineData("30000/1001", 29.97)]
    [InlineData("25/1", 25)]
    [InlineData("24", 24)]
    [InlineData("0/0", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("valor-estranho", 0)]
    public void Converte_a_taxa_de_quadros_informada_como_fracao(string? valor, double esperado)
    {
        Assert.Equal(esperado, FfprobeOutputParser.Fracao(valor), 2);
    }
}
