// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Globalization;
using OpenTube.Domain.Media;

namespace OpenTube.Worker.Media;

/// <summary>
/// Monta as linhas de comando do FFmpeg. É código puro de propósito: a linha de comando é a
/// parte mais fácil de errar e a mais cara de descobrir errada só na produção.
/// </summary>
public static class FfmpegArguments
{
    /// <summary>
    /// Padrão do arquivo de inicialização de cada versão, no formato fMP4. Sem o marcador da
    /// versão, o FFmpeg numera os arquivos por conta própria (<c>init_0.mp4</c>) e o nome
    /// deixa de ser previsível a partir do nome da versão.
    /// </summary>
    public const string InitFileNamePattern = "init-%v.mp4";

    /// <summary>Nome do arquivo de inicialização de uma versão específica.</summary>
    public static string InitFileNameFor(string renditionName) => $"init-{renditionName}.mp4";

    /// <summary>Nome da playlist principal gerada pelo FFmpeg.</summary>
    public const string MasterFileName = "master.m3u8";

    public static IReadOnlyList<string> Probe(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        return
        [
            "-v", "error",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            inputPath
        ];
    }

    /// <summary>
    /// Transcodifica para HLS em uma única passagem: o original é decodificado uma vez e
    /// dividido entre as versões, em vez de ser lido do zero para cada uma.
    /// </summary>
    public static IReadOnlyList<string> Transcode(
        string inputPath,
        string outputDirectory,
        IReadOnlyList<Rendition> ladder,
        double frameRate,
        bool hasAudio)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(ladder);

        if (ladder.Count == 0)
            throw new ArgumentException("É preciso ao menos uma versão para transcodificar.", nameof(ladder));

        var gop = RenditionLadder.KeyFrameInterval(frameRate);
        var argumentos = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", inputPath,
            "-filter_complex", FiltroDeEscala(ladder)
        };

        for (var i = 0; i < ladder.Count; i++)
        {
            var versao = ladder[i];

            argumentos.AddRange(["-map", $"[v{i}out]"]);
            argumentos.AddRange([$"-c:v:{i}", "libx264"]);
            argumentos.AddRange([$"-preset", "veryfast"]);
            argumentos.AddRange([$"-profile:v:{i}", versao.Height >= 720 ? "high" : "main"]);
            argumentos.AddRange([$"-b:v:{i}", Kbps(versao.VideoBitrateKbps)]);
            argumentos.AddRange([$"-maxrate:v:{i}", Kbps(versao.MaxRateKbps)]);
            argumentos.AddRange([$"-bufsize:v:{i}", Kbps(versao.BufferSizeKbps)]);
        }

        if (hasAudio)
        {
            for (var i = 0; i < ladder.Count; i++)
            {
                argumentos.AddRange(["-map", "a:0"]);
                argumentos.AddRange([$"-c:a:{i}", "aac"]);
                argumentos.AddRange([$"-b:a:{i}", Kbps(ladder[i].AudioBitrateKbps)]);
                argumentos.AddRange([$"-ac:a:{i}", "2"]);
            }
        }

        argumentos.AddRange([
            // Quadros-chave alinhados entre as versões: sem isso o player trava ao trocar
            // de qualidade no meio da reprodução.
            "-g", gop.ToString(CultureInfo.InvariantCulture),
            "-keyint_min", gop.ToString(CultureInfo.InvariantCulture),
            "-sc_threshold", "0",
            "-f", "hls",
            "-hls_time", RenditionLadder.SegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-hls_playlist_type", "vod",
            "-hls_segment_type", "fmp4",
            "-hls_flags", "independent_segments",
            "-hls_fmp4_init_filename", InitFileNamePattern,
            "-hls_segment_filename", Path.Combine(outputDirectory, "%v", "seg-%05d.m4s"),
            "-master_pl_name", MasterFileName,
            "-var_stream_map", MapaDeVersoes(ladder, hasAudio),
            Path.Combine(outputDirectory, "%v", "stream.m3u8")
        ]);

        return argumentos;
    }

    public static IReadOnlyList<string> Thumbnail(string inputPath, string outputPath, double atSecond, int width = 640)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentOutOfRangeException.ThrowIfNegative(atSecond);

        return
        [
            "-y", "-hide_banner", "-loglevel", "error",
            // O posicionamento vem antes da entrada para que o FFmpeg salte direto ao
            // ponto, em vez de decodificar tudo até lá.
            "-ss", Segundos(atSecond),
            "-i", inputPath,
            "-frames:v", "1",
            "-vf", $"scale={width}:-2",
            "-q:v", "3",
            outputPath
        ];
    }

    public static IReadOnlyList<string> Sprite(string inputPath, string outputPath, SpriteLayout layout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var filtro = string.Create(CultureInfo.InvariantCulture,
            $"fps=1/{layout.IntervalSeconds},scale={layout.ThumbWidth}:{layout.ThumbHeight},tile={SpriteLayout.ColumnCount}x{layout.Rows}");

        return
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", inputPath,
            "-vf", filtro,
            "-frames:v", "1",
            "-q:v", "4",
            outputPath
        ];
    }

    /// <summary>
    /// Divide o fluxo de vídeo em tantas cópias quantas forem as versões e redimensiona cada
    /// uma. A proporção do original é preservada, e o resultado é forçado a dimensões pares.
    /// </summary>
    private static string FiltroDeEscala(IReadOnlyList<Rendition> ladder)
    {
        var entradas = string.Concat(Enumerable.Range(0, ladder.Count).Select(i => $"[v{i}]"));
        var partes = new List<string> { $"[0:v]split={ladder.Count}{entradas}" };

        for (var i = 0; i < ladder.Count; i++)
        {
            partes.Add($"[v{i}]scale=w={ladder[i].Width}:h={ladder[i].Height}:force_original_aspect_ratio=decrease,"
                       + $"pad=ceil(iw/2)*2:ceil(ih/2)*2,setsar=1[v{i}out]");
        }

        return string.Join(';', partes);
    }

    /// <summary>
    /// Diz ao FFmpeg quais fluxos formam cada versão e como nomear sua pasta. O nome vira o
    /// diretório no storage, o que mantém os caminhos legíveis.
    /// </summary>
    private static string MapaDeVersoes(IReadOnlyList<Rendition> ladder, bool hasAudio) =>
        string.Join(' ', ladder.Select((versao, i) => hasAudio
            ? $"v:{i},a:{i},name:{versao.Name}"
            : $"v:{i},name:{versao.Name}"));

    private static string Kbps(int valor) => valor.ToString(CultureInfo.InvariantCulture) + "k";

    private static string Segundos(double valor) => valor.ToString("0.###", CultureInfo.InvariantCulture);
}
