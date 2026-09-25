using Microsoft.Extensions.Options;

namespace OpenTube.Worker.Media;

/// <summary>Descobre as características do arquivo enviado.</summary>
public interface IMediaProbe
{
    Task<MediaInfo> InspectAsync(string filePath, CancellationToken cancellationToken = default);
}

public class FfprobeMediaProbe(IProcessRunner runner, IOptions<MediaToolOptions> options) : IMediaProbe
{
    private readonly MediaToolOptions _options = options.Value;

    public async Task<MediaInfo> InspectAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var resultado = await runner.RunAsync(_options.FfprobePath, FfmpegArguments.Probe(filePath), cancellationToken);

        if (!resultado.Succeeded)
            throw new InvalidOperationException($"Não foi possível ler o arquivo enviado: {resultado.ShortError()}");

        return FfprobeOutputParser.Parse(resultado.StandardOutput);
    }
}
