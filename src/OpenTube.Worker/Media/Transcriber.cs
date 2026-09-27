using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenTube.Worker.Media;

/// <summary>Resultado de uma transcrição.</summary>
/// <param name="VttPath">Arquivo de legenda gerado.</param>
/// <param name="Text">Texto falado, já sem marcação.</param>
/// <param name="Language">Idioma usado.</param>
public sealed record Transcription(string VttPath, string Text, string Language);

/// <summary>Transcrição do áudio em legenda. Isolada para permitir trocar a ferramenta.</summary>
public interface ITranscriber
{
    /// <summary>Se a ferramenta está configurada nesta instalação.</summary>
    bool IsAvailable { get; }

    /// <param name="language">Código do idioma falado, no formato da ferramenta (<c>pt</c>, <c>en</c>).</param>
    Task<Transcription> TranscribeAsync(string mediaPath, string workDirectory, string language, CancellationToken cancellationToken = default);
}

/// <summary>Ajustes da transcrição automática.</summary>
public class TranscriptionOptions
{
    public const string SectionName = "Transcription";

    /// <summary>
    /// Programa de transcrição. Vazio desliga o recurso, que é o padrão: a transcrição é a
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

    /// <summary>Tempo máximo de uma transcrição.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromHours(4);
}

/// <summary>
/// Transcrição por programa de linha de comando. A forma dos argumentos fica em configuração
/// porque cada ferramenta tem a sua, e trocar de ferramenta não deveria exigir recompilar.
/// </summary>
public class CommandLineTranscriber(
    IProcessRunner runner,
    IOptions<TranscriptionOptions> options,
    IOptions<MediaToolOptions> mediaOptions,
    ILogger<CommandLineTranscriber> logger) : ITranscriber
{
    private readonly TranscriptionOptions _options = options.Value;
    private readonly MediaToolOptions _media = mediaOptions.Value;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_options.Executable);

    public async Task<Transcription> TranscribeAsync(
        string mediaPath, string workDirectory, string language, CancellationToken cancellationToken = default)
    {
        var idioma = string.IsNullOrWhiteSpace(language) ? _options.Language : language.Trim();

        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);

        if (!IsAvailable)
            throw new InvalidOperationException("A transcrição automática não está configurada nesta instalação.");

        // O reconhecimento de fala trabalha com áudio mono em 16 kHz; entregar o vídeo
        // inteiro faria a ferramenta extrair isso de novo a cada execução.
        var audio = Path.Combine(workDirectory, "audio.wav");

        var extracao = await runner.RunAsync(_media.FfmpegPath, [
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", mediaPath,
            "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le",
            audio
        ], cancellationToken);

        if (!extracao.Succeeded)
            throw new InvalidOperationException($"Não foi possível extrair o áudio: {extracao.ShortError()}");

        var saida = Path.Combine(workDirectory, "legenda");

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(_options.Timeout);

        var resultado = await runner.RunAsync(
            _options.Executable!, MontarArgumentos(audio, saida, idioma), limite.Token);

        if (!resultado.Succeeded)
            throw new InvalidOperationException($"Falha na transcrição: {resultado.ShortError()}");

        var vtt = EncontrarVtt(workDirectory, saida)
            ?? throw new InvalidOperationException("A transcrição terminou sem produzir um arquivo de legenda.");

        var conteudo = await File.ReadAllTextAsync(vtt, cancellationToken);

        logger.LogInformation("Transcrição concluída com {Caracteres} caracteres de fala", conteudo.Length);

        return new Transcription(vtt, VttParser.ExtractText(conteudo), idioma);
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
}
