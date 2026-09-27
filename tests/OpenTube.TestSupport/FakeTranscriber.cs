using OpenTube.Worker.Media;

namespace OpenTube.TestSupport;

/// <summary>Transcrição controlada pelo teste, sem depender da ferramenta instalada.</summary>
public class FakeTranscriber : ITranscriber
{
    public bool IsAvailable { get; set; } = true;

    /// <summary>Conteúdo do arquivo de legenda produzido.</summary>
    public string Vtt { get; set; } = "WEBVTT\n\n00:00:01.000 --> 00:00:04.000\nBom dia a todos.\n";

    /// <summary>Idioma recebido na última chamada.</summary>
    public string? IdiomaPedido { get; private set; }

    public Exception? Falha { get; set; }

    public int Chamadas { get; private set; }

    /// <summary>Idioma "detectado" quando o pedido é <c>auto</c>.</summary>
    public string IdiomaDetectado { get; set; } = "pt";

    public Task<TranscriberStatus> CheckAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(IsAvailable ? new TranscriberStatus(true, "fake") : TranscriberStatus.Off);

    public async Task<Transcription> TranscribeAsync(
        string mediaPath, string workDirectory, string language, CancellationToken cancellationToken = default)
    {
        Chamadas++;
        IdiomaPedido = language;

        if (Falha is not null)
            throw Falha;

        var caminho = Path.Combine(workDirectory, "legenda.vtt");
        await File.WriteAllTextAsync(caminho, Vtt, cancellationToken);

        return new Transcription(caminho, VttParser.ExtractText(Vtt), language == "auto" ? IdiomaDetectado : language);
    }
}
