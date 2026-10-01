// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.Extensions.Logging.Abstractions;
using OpenTube.TestSupport;
using OpenTube.Worker.Media;

namespace OpenTube.Worker.Tests.Media;

/// <summary>
/// Exercita o pipeline contra o FFmpeg de verdade. Argumento de linha de comando errado só
/// aparece aqui: o construtor de argumentos passa nos testes e o FFmpeg recusa na execução.
/// </summary>
public class TranscodePipelineTests : IDisposable
{
    private readonly string _trabalho = Directory.CreateTempSubdirectory("opentube-testes-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_trabalho, recursive: true);
        }
        catch (IOException)
        {
            // Pasta temporária ocupada; o sistema operacional limpa depois.
        }
    }

    private static TranscodePipeline Criar()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var opcoes = Microsoft.Extensions.Options.Options.Create(new MediaToolOptions());

        return new TranscodePipeline(runner, new FfprobeMediaProbe(runner, opcoes), opcoes, NullLogger<TranscodePipeline>.Instance);
    }

    [FfmpegFact]
    public async Task Gera_o_ladder_completo_com_playlist_principal()
    {
        var entrada = await MediaTools.CreateSampleAsync(_trabalho, seconds: 6, width: 1280, height: 720);

        var saida = await Criar().RunAsync(entrada, _trabalho);

        Assert.Equal(1280, saida.Info.Width);
        Assert.Equal(720, saida.Info.Height);
        Assert.True(saida.Info.HasAudio);
        Assert.Equal(["360p", "480p", "720p"], saida.Ladder.Select(r => r.Name));

        var master = Path.Combine(saida.OutputDirectory, FfmpegArguments.MasterFileName);
        Assert.True(File.Exists(master), "a playlist principal deveria existir");

        var conteudo = await File.ReadAllTextAsync(master);
        Assert.Contains("#EXTM3U", conteudo);
        Assert.Contains("360p/stream.m3u8", conteudo);
        Assert.Contains("720p/stream.m3u8", conteudo);
    }

    [FfmpegFact]
    public async Task Cada_versao_tem_playlist_inicializacao_e_segmentos()
    {
        var entrada = await MediaTools.CreateSampleAsync(_trabalho, seconds: 6, width: 1280, height: 720);

        var saida = await Criar().RunAsync(entrada, _trabalho);

        foreach (var versao in saida.Ladder)
        {
            var pasta = Path.Combine(saida.OutputDirectory, versao.Name);

            Assert.True(File.Exists(Path.Combine(pasta, "stream.m3u8")), $"playlist de {versao.Name}");
            Assert.True(File.Exists(Path.Combine(pasta, FfmpegArguments.InitFileNameFor(versao.Name))), $"inicialização de {versao.Name}");
            Assert.NotEmpty(Directory.GetFiles(pasta, "seg-*.m4s"));
        }
    }

    [FfmpegFact]
    public async Task Produz_miniatura_folha_de_previa_e_o_arquivo_de_trechos()
    {
        var entrada = await MediaTools.CreateSampleAsync(_trabalho, seconds: 8, width: 640, height: 360);

        var saida = await Criar().RunAsync(entrada, _trabalho);

        Assert.True(new FileInfo(saida.ThumbnailPath).Length > 0);
        Assert.True(new FileInfo(saida.SpritePath).Length > 0);

        var vtt = await File.ReadAllTextAsync(saida.SpriteVttPath);
        Assert.StartsWith("WEBVTT", vtt);
        Assert.Contains("#xywh=", vtt);
    }

    [FfmpegFact]
    public async Task Video_sem_audio_gera_playlist_valida()
    {
        var entrada = await MediaTools.CreateSampleAsync(_trabalho, seconds: 5, width: 640, height: 360, withAudio: false);

        var saida = await Criar().RunAsync(entrada, _trabalho);

        Assert.False(saida.Info.HasAudio);
        var master = await File.ReadAllTextAsync(Path.Combine(saida.OutputDirectory, FfmpegArguments.MasterFileName));
        Assert.Contains("360p/stream.m3u8", master);
    }

    [FfmpegFact]
    public async Task Video_em_pe_mantem_a_orientacao_nas_saidas()
    {
        var entrada = await MediaTools.CreateSampleAsync(_trabalho, seconds: 5, width: 720, height: 1280);

        var saida = await Criar().RunAsync(entrada, _trabalho);

        Assert.True(saida.Info.IsPortrait);
        Assert.Equal(["360p", "480p", "720p"], saida.Ladder.Select(r => r.Name));
        Assert.All(saida.Ladder, versao => Assert.True(versao.Height > versao.Width));
    }

    [FfmpegFact]
    public async Task Video_pequeno_gera_uma_versao_unica_na_resolucao_nativa()
    {
        var entrada = await MediaTools.CreateSampleAsync(_trabalho, seconds: 4, width: 426, height: 240);

        var saida = await Criar().RunAsync(entrada, _trabalho);

        var unica = Assert.Single(saida.Ladder);
        Assert.Equal("240p", unica.Name);
        Assert.True(File.Exists(Path.Combine(saida.OutputDirectory, "240p", "stream.m3u8")));
    }

    [FfmpegFact]
    public async Task Recusa_arquivo_que_nao_e_video()
    {
        var falso = Path.Combine(_trabalho, "falso.mp4");
        await File.WriteAllTextAsync(falso, "isto não é um vídeo");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Criar().RunAsync(falso, _trabalho));
    }

    [FfmpegFact]
    public async Task Recusa_arquivo_inexistente()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Criar().RunAsync(Path.Combine(_trabalho, "nao-existe.mp4"), _trabalho));
    }

    [FfmpegFact]
    public async Task Recusa_playlist_que_manda_ler_outro_arquivo_local()
    {
        // O "segredo" é um vídeo válido ao lado do envio: sem a restrição, o FFmpeg seguiria a
        // playlist, leria o arquivo e o publicaria como se fosse o vídeo enviado.
        await MediaTools.CreateSampleAsync(_trabalho, seconds: 2, width: 320, height: 240, fileName: "segredo.mp4");
        var playlist = Path.Combine(_trabalho, "enviado.m3u8");
        await File.WriteAllTextAsync(playlist,
            "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\nsegredo.mp4\n#EXT-X-ENDLIST\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Criar().RunAsync(playlist, _trabalho));
    }

    [FfmpegFact]
    public async Task Aceita_os_conteineres_oferecidos_no_envio()
    {
        foreach (var extensao in new[] { "mkv", "webm", "avi", "mpg", "ts", "flv", "wmv", "mov" })
        {
            var amostra = await MediaTools.CreateSampleAsync(_trabalho, seconds: 2, width: 320, height: 240, fileName: "base.mp4");
            var convertido = Path.Combine(_trabalho, "envio." + extensao);
            var conversao = await new ProcessRunner(NullLogger<ProcessRunner>.Instance)
                .RunAsync("ffmpeg", ["-y", "-loglevel", "error", "-i", amostra, convertido]);
            Assert.True(conversao.Succeeded, conversao.ShortError());

            var pasta = Path.Combine(_trabalho, "saida-" + extensao);
            var saida = await Criar().RunAsync(convertido, pasta);

            Assert.True(saida.Info.DurationSeconds > 0, extensao);
        }
    }
}
