// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenTube.Worker.Media;

/// <summary>Resultado de uma transcrição.</summary>
/// <param name="VttPath">Arquivo de legenda gerado.</param>
/// <param name="Text">Texto falado, já sem marcação.</param>
/// <param name="Language">Idioma da fala: o pedido, ou o detectado quando o pedido era "auto".</param>
public sealed record Transcription(string VttPath, string Text, string Language);

/// <summary>Se a transcrição pode ser feita agora, e com o quê.</summary>
/// <param name="Available">Pronta para transcrever.</param>
/// <param name="Engine">Descrição da ferramenta, do modelo e da aceleração.</param>
public sealed record TranscriberStatus(bool Available, string? Engine)
{
    public static TranscriberStatus Off { get; } = new(false, null);
}

/// <summary>Transcrição do áudio em legenda. Isolada para permitir trocar a ferramenta.</summary>
public interface ITranscriber
{
    /// <summary>Se a ferramenta está configurada nesta instalação.</summary>
    bool IsAvailable { get; }

    /// <summary>Confere se a ferramenta responde agora, para a aplicação saber se oferece o recurso.</summary>
    Task<TranscriberStatus> CheckAsync(CancellationToken cancellationToken = default);

    /// <param name="language">Código do idioma (<c>pt</c>, <c>en</c>) ou <c>auto</c> para detectar.</param>
    Task<Transcription> TranscribeAsync(string mediaPath, string workDirectory, string language, CancellationToken cancellationToken = default);
}

/// <summary>Ajustes da transcrição automática.</summary>
public class TranscriptionOptions
{
    public const string SectionName = "Transcription";

    /// <summary>
    /// Endereço do servidor do Whisper (o container <c>whisper</c>, em produção). Quando
    /// definido, tem preferência sobre o programa de linha de comando.
    /// </summary>
    public string? ServerUrl { get; set; }

    /// <summary>
    /// Programa de transcrição. Vazio (e sem servidor) desliga o recurso: a transcrição é a
    /// etapa mais cara em processamento de todo o pipeline.
    /// </summary>
    public string? Executable { get; set; }

    /// <summary>
    /// Argumentos, com marcadores substituídos na execução: <c>{entrada}</c>, <c>{saida}</c>,
    /// <c>{idioma}</c> e <c>{modelo}</c>.
    /// </summary>
    public string Arguments { get; set; } = "-m {modelo} -f {entrada} -l {idioma} -ovtt -of {saida}";

    /// <summary>Caminho do modelo, quando a ferramenta precisar de um.</summary>
    public string? ModelPath { get; set; }

    /// <summary>Idioma usado quando o pedido não traz um (trabalhos antigos na fila).</summary>
    public string Language { get; set; } = "pt";

    /// <summary>Tempo mínimo concedido a uma transcrição, por mais curto que seja o áudio.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromHours(4);

    /// <summary>
    /// Quantas vezes a duração do áudio uma transcrição pode levar. Numa CPU sem AVX2 o Whisper
    /// chega a gastar quase 4 minutos por minuto de fala; um limite fixo derrubaria justo os
    /// vídeos longos.
    /// </summary>
    public double TimeoutFactor { get; set; } = 10;

    /// <summary>Limite para um áudio desta duração: o maior entre o mínimo e o proporcional.</summary>
    public TimeSpan LimitFor(TimeSpan audio)
    {
        var proporcional = audio * Math.Max(TimeoutFactor, 1);
        return proporcional > Timeout ? proporcional : Timeout;
    }
}

/// <summary>Extração do áudio no formato que o Whisper usa: mono, 16 kHz, PCM.</summary>
public static class AudioForTranscription
{
    public static async Task<string> ExtractAsync(
        IProcessRunner runner, string ffmpeg, string mediaPath, string workDirectory, CancellationToken cancellationToken)
    {
        // Entregar o vídeo inteiro faria a ferramenta extrair o áudio de novo a cada execução.
        var audio = Path.Combine(workDirectory, "audio.wav");

        var extracao = await runner.RunAsync(ffmpeg, [
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", mediaPath,
            "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le",
            audio
        ], cancellationToken);

        if (!extracao.Succeeded)
            throw new InvalidOperationException($"Não foi possível extrair o áudio: {extracao.ShortError()}");

        return audio;
    }

    /// <summary>
    /// Duração do áudio extraído, pelo tamanho: PCM de 16 bits, mono, 16 kHz são 32.000 bytes
    /// por segundo, depois dos 44 bytes do cabeçalho WAV.
    /// </summary>
    public static TimeSpan Duration(string wavPath)
    {
        var bytes = Math.Max(0, new FileInfo(wavPath).Length - 44);
        return TimeSpan.FromSeconds(bytes / 32000d);
    }
}

/// <summary>
/// Transcrição por programa de linha de comando. A forma dos argumentos fica em configuração
/// porque cada ferramenta tem a sua, e trocar de ferramenta não deveria exigir recompilar.
/// </summary>
public partial class CommandLineTranscriber(
    IProcessRunner runner,
    IOptions<TranscriptionOptions> options,
    IOptions<MediaToolOptions> mediaOptions,
    ILogger<CommandLineTranscriber> logger) : ITranscriber
{
    private readonly TranscriptionOptions _options = options.Value;
    private readonly MediaToolOptions _media = mediaOptions.Value;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_options.Executable);

    public Task<TranscriberStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return Task.FromResult(TranscriberStatus.Off);

        // O programa e o modelo precisam existir de fato: configuração apontando para o vazio
        // só apareceria como falha na hora de transcrever.
        var programa = Path.IsPathRooted(_options.Executable!) ? File.Exists(_options.Executable) : true;
        var modelo = string.IsNullOrWhiteSpace(_options.ModelPath) || File.Exists(_options.ModelPath);

        return Task.FromResult(programa && modelo
            ? new TranscriberStatus(true, $"{Path.GetFileName(_options.Executable)} · {Path.GetFileName(_options.ModelPath ?? string.Empty)}".TrimEnd(' ', '·'))
            : TranscriberStatus.Off);
    }

    public async Task<Transcription> TranscribeAsync(
        string mediaPath, string workDirectory, string language, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);

        if (!IsAvailable)
            throw new InvalidOperationException("A transcrição automática não está configurada nesta instalação.");

        var idioma = string.IsNullOrWhiteSpace(language) ? _options.Language : language.Trim();
        var audio = await AudioForTranscription.ExtractAsync(runner, _media.FfmpegPath, mediaPath, workDirectory, cancellationToken);
        var saida = Path.Combine(workDirectory, "legenda");

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(_options.LimitFor(AudioForTranscription.Duration(audio)));

        var resultado = await runner.RunAsync(
            _options.Executable!, MontarArgumentos(audio, saida, idioma), limite.Token);

        if (!resultado.Succeeded)
            throw new InvalidOperationException($"Falha na transcrição: {resultado.ShortError()}");

        var vtt = EncontrarVtt(workDirectory, saida)
            ?? throw new InvalidOperationException("A transcrição terminou sem produzir um arquivo de legenda.");

        var conteudo = await File.ReadAllTextAsync(vtt, cancellationToken);

        // Na detecção automática, o whisper-cli informa o idioma no próprio log.
        var falado = idioma;
        if (idioma == "auto")
        {
            var achado = IdiomaDetectado().Match(resultado.StandardError + "\n" + resultado.StandardOutput);
            falado = achado.Success ? achado.Groups[1].Value : _options.Language;
        }

        logger.LogInformation("Transcrição concluída ({Idioma}) com {Caracteres} caracteres de fala", falado, conteudo.Length);

        return new Transcription(vtt, VttParser.ExtractText(conteudo), falado);
    }

    /// <summary>Substitui os marcadores pelos caminhos reais, um argumento por vez.</summary>
    public IReadOnlyList<string> MontarArgumentos(string entrada, string saida, string? idioma = null) =>
        [.. _options.Arguments
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(argumento => argumento
                .Replace("{entrada}", entrada, StringComparison.Ordinal)
                .Replace("{saida}", saida, StringComparison.Ordinal)
                .Replace("{idioma}", string.IsNullOrWhiteSpace(idioma) ? _options.Language : idioma, StringComparison.Ordinal)
                .Replace("{modelo}", _options.ModelPath ?? string.Empty, StringComparison.Ordinal))];

    /// <summary>
    /// Encontra o arquivo gerado. Cada ferramenta acrescenta a própria extensão ao nome de
    /// saída, então vale procurar pelo que apareceu na pasta.
    /// </summary>
    private static string? EncontrarVtt(string workDirectory, string baseName)
    {
        var direto = baseName + ".vtt";

        if (File.Exists(direto))
            return direto;

        return Directory.EnumerateFiles(workDirectory, "*.vtt", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    [GeneratedRegex(@"auto-detected language:\s*([a-z]{2,3})")]
    private static partial Regex IdiomaDetectado();
}
