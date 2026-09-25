using OpenTube.Worker.Media;

namespace OpenTube.TestSupport;

/// <summary>Transcrição controlada pelo teste, sem depender da ferramenta instalada.</summary>
public class FakeTranscriber : ITranscriber
{
    public bool IsAvailable { get; set; } = true;

    /// <summary>Conteúdo do arquivo de legenda produzido.</summary>
    public string Vtt { get; set; } = "WEBVTT\n\n00:00:01.000 --> 00:00:04.000\nBom dia a todos.\n";

    public string Language { get; set; } = "pt";

    public Exception? Falha { get; set; }

    public int Chamadas { get; private set; }

    public async Task<Transcription> TranscribeAsync(
        string mediaPath, string workDirectory, CancellationToken cancellationToken = default)
    {
        Chamadas++;

        if (Falha is not null)
            throw Falha;

        var caminho = Path.Combine(workDirectory, "legenda.vtt");
        await File.WriteAllTextAsync(caminho, Vtt, cancellationToken);

        return new Transcription(caminho, VttParser.ExtractText(Vtt), Language);
    }
}
