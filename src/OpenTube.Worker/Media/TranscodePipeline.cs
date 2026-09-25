using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Media;

namespace OpenTube.Worker.Media;

/// <summary>O que a transcodificação produziu em disco.</summary>
/// <param name="Info">Características do arquivo original.</param>
/// <param name="Ladder">Versões geradas.</param>
/// <param name="OutputDirectory">Pasta com a playlist principal e as versões.</param>
/// <param name="ThumbnailPath">Miniatura de capa.</param>
/// <param name="SpritePath">Folha de miniaturas da barra de progresso.</param>
/// <param name="SpriteVttPath">Arquivo que liga instantes aos recortes da folha.</param>
public sealed record TranscodeOutput(
    MediaInfo Info,
    IReadOnlyList<Rendition> Ladder,
    string OutputDirectory,
    string ThumbnailPath,
    string SpritePath,
    string SpriteVttPath);

/// <summary>
/// Converte o arquivo enviado em HLS adaptativo e nos derivados de apoio. Trabalha só com
/// arquivos locais: baixar e enviar é responsabilidade de quem chama.
/// </summary>
public class TranscodePipeline(
    IProcessRunner runner,
    IMediaProbe probe,
    IOptions<MediaToolOptions> options,
    ILogger<TranscodePipeline> logger)
{
    public const string ThumbnailFileName = "thumb.jpg";
    public const string SpriteFileName = "sprite.jpg";
    public const string SpriteVttFileName = "sprite.vtt";

    private readonly MediaToolOptions _options = options.Value;

    public async Task<TranscodeOutput> RunAsync(string inputPath, string workDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);

        var info = await probe.InspectAsync(inputPath, cancellationToken);

        if (info.DurationSeconds <= 0)
            throw new InvalidOperationException("O arquivo enviado não tem duração utilizável.");

        var ladder = RenditionLadder.For(info.Width, info.Height);
        var saida = Path.Combine(workDirectory, "hls");

        Directory.CreateDirectory(saida);
        foreach (var versao in ladder)
            Directory.CreateDirectory(Path.Combine(saida, versao.Name));

        logger.LogInformation(
            "Transcodificando {Largura}x{Altura} de {Duracao:0.#}s em {Versoes} versões",
            info.Width, info.Height, info.DurationSeconds, ladder.Count);

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(_options.TranscodeTimeout);

        await ExecutarAsync(
            FfmpegArguments.Transcode(inputPath, saida, ladder, info.FrameRate, info.HasAudio),
            "transcodificação",
            limite.Token);

        var miniatura = Path.Combine(workDirectory, ThumbnailFileName);
        // Dez por cento da duração evita a tela preta comum no primeiro quadro.
        var instante = Math.Max(1, info.DurationSeconds * 0.1);
        await ExecutarAsync(FfmpegArguments.Thumbnail(inputPath, miniatura, instante), "miniatura", cancellationToken);

        var disposicao = SpriteLayout.For(info.DurationSeconds, info.Width, info.Height);
        var folha = Path.Combine(workDirectory, SpriteFileName);
        await ExecutarAsync(FfmpegArguments.Sprite(inputPath, folha, disposicao), "folha de miniaturas", cancellationToken);

        var vtt = Path.Combine(workDirectory, SpriteVttFileName);
        await File.WriteAllTextAsync(vtt, SpriteVtt.Build(disposicao, SpriteFileName, info.DurationSeconds), cancellationToken);

        return new TranscodeOutput(info, ladder, saida, miniatura, folha, vtt);
    }

    private async Task ExecutarAsync(IReadOnlyList<string> argumentos, string etapa, CancellationToken cancellationToken)
    {
        var resultado = await runner.RunAsync(_options.FfmpegPath, argumentos, cancellationToken);

        if (!resultado.Succeeded)
            throw new InvalidOperationException($"Falha na {etapa}: {resultado.ShortError()}");
    }
}
