using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Captions;

namespace OpenTube.Worker.Media;

/// <summary>
/// Cliente HTTP do servidor do Whisper, compartilhado. Sem limite de tempo próprio: uma
/// transcrição longa dura o que durar, e o limite vem de <see cref="TranscriptionOptions.Timeout"/>.
/// </summary>
public sealed class WhisperHttp : IDisposable
{
    public WhisperHttp() : this(new HttpClientHandler())
    {
    }

    /// <summary>Com o tratador de requisições dado; os testes o usam para simular o servidor.</summary>
    public WhisperHttp(HttpMessageHandler handler) =>
        Client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

    public HttpClient Client { get; }

    public void Dispose() => Client.Dispose();
}

/// <summary>
/// Transcrição pelo servidor do whisper.cpp — o container <c>whisper</c> em produção, ou o
/// <c>whisper-server</c> local no <c>make watch</c>. O worker só extrai o áudio e envia; o
/// modelo, a GPU ou as threads de CPU são problema do container, que os escolhe ao subir e os
/// publica em <c>/info.json</c>.
/// </summary>
public class WhisperServerTranscriber(
    IProcessRunner runner,
    WhisperHttp http,
    IOptions<TranscriptionOptions> options,
    IOptions<MediaToolOptions> mediaOptions,
    ILogger<WhisperServerTranscriber> logger) : ITranscriber
{
    private static readonly TimeSpan TempoDaVerificacao = TimeSpan.FromSeconds(5);

    private readonly TranscriptionOptions _options = options.Value;
    private readonly MediaToolOptions _media = mediaOptions.Value;

    private string Base => _options.ServerUrl!.TrimEnd('/');

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_options.ServerUrl);

    public async Task<TranscriberStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return TranscriberStatus.Off;

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(TempoDaVerificacao);

        try
        {
            // O servidor só responde "ok" depois de carregar o modelo; enquanto baixa ou
            // carrega, a transcrição ainda não está disponível.
            using var saude = await http.Client.GetAsync($"{Base}/health", limite.Token);
            if (!saude.IsSuccessStatusCode)
                return TranscriberStatus.Off;

            return new TranscriberStatus(true, await DescreverAsync(limite.Token));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return TranscriberStatus.Off;
        }
    }

    public async Task<Transcription> TranscribeAsync(
        string mediaPath, string workDirectory, string language, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);

        if (!IsAvailable)
            throw new InvalidOperationException("A transcrição automática não está configurada nesta instalação.");

        var idioma = string.IsNullOrWhiteSpace(language) ? "auto" : language.Trim();
        var audio = await AudioForTranscription.ExtractAsync(runner, _media.FfmpegPath, mediaPath, workDirectory, cancellationToken);

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(_options.Timeout);

        await using var arquivo = File.OpenRead(audio);
        using var formulario = new MultipartFormDataContent
        {
            { new StreamContent(arquivo), "file", "audio.wav" },
            { new StringContent("verbose_json"), "response_format" },
            { new StringContent(idioma), "language" },
            { new StringContent("0.0"), "temperature" }
        };

        using var resposta = await http.Client.PostAsync($"{Base}/inference", formulario, limite.Token);
        var corpo = await resposta.Content.ReadAsStringAsync(limite.Token);

        if (!resposta.IsSuccessStatusCode)
            throw new InvalidOperationException($"Falha na transcrição ({(int)resposta.StatusCode}): {Resumir(corpo)}");

        var resultado = System.Text.Json.JsonSerializer.Deserialize<RespostaDoWhisper>(corpo)
            ?? throw new InvalidOperationException("O servidor do Whisper respondeu sem conteúdo.");

        if (!string.IsNullOrWhiteSpace(resultado.Erro))
            throw new InvalidOperationException($"Falha na transcrição: {resultado.Erro}");

        var legenda = CaptionDocument.FromCues((resultado.Trechos ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.Texto))
            .Select(t => new CaptionCue(
                TimeSpan.FromSeconds(Math.Max(0, t.Inicio)),
                // Trecho sem duração seria recusado pela legenda: ganha um instante mínimo.
                TimeSpan.FromSeconds(Math.Max(t.Fim, Math.Max(0, t.Inicio) + 0.1)),
                t.Texto!.Trim())));

        var falado = idioma == "auto"
            ? WhisperLanguages.ToCode(resultado.Idioma) ?? _options.Language
            : idioma;

        var vtt = Path.Combine(workDirectory, "legenda.vtt");
        await File.WriteAllTextAsync(vtt, legenda.ToWebVtt(), cancellationToken);

        logger.LogInformation(
            "Transcrição concluída pelo servidor: idioma {Idioma}, {Trechos} trechos", falado, legenda.Cues.Count);

        return new Transcription(vtt, legenda.PlainText, falado);
    }

    /// <summary>O que o container escolheu ao subir: aceleração, modelo e threads.</summary>
    private async Task<string> DescreverAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = await http.Client.GetFromJsonAsync<InfoDoWhisper>($"{Base}/info.json", cancellationToken);

            if (info is null)
                return "whisper.cpp";

            var partes = new[]
            {
                "whisper.cpp",
                info.Aceleracao?.ToUpperInvariant(),
                info.Modelo,
                info.Threads is { } t ? string.Create(CultureInfo.InvariantCulture, $"{t} threads") : null
            };

            return string.Join(" · ", partes.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Servidor sem o arquivo de informações (o whisper-server local, por exemplo).
            return "whisper.cpp";
        }
    }

    private static string Resumir(string texto) => texto.Length <= 500 ? texto : texto[..500];

    private sealed record RespostaDoWhisper(
        [property: JsonPropertyName("language")] string? Idioma,
        [property: JsonPropertyName("segments")] List<TrechoDoWhisper>? Trechos,
        [property: JsonPropertyName("error")] string? Erro);

    private sealed record TrechoDoWhisper(
        [property: JsonPropertyName("start")] double Inicio,
        [property: JsonPropertyName("end")] double Fim,
        [property: JsonPropertyName("text")] string? Texto);

    private sealed record InfoDoWhisper(
        [property: JsonPropertyName("acceleration")] string? Aceleracao,
        [property: JsonPropertyName("model")] string? Modelo,
        [property: JsonPropertyName("threads")] int? Threads);
}
