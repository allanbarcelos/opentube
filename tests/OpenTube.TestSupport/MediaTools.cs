using System.Diagnostics;

namespace OpenTube.TestSupport;

/// <summary>
/// Apoio para os testes que exercitam o pipeline de mídia de verdade. Gerar um vídeo de
/// amostra com o próprio FFmpeg evita guardar binários no repositório e permite pedir
/// exatamente a duração e a resolução que o teste precisa.
/// </summary>
public static class MediaTools
{
    private static readonly Lazy<bool> Disponivel = new(() => Existe("ffmpeg") && Existe("ffprobe"));

    public static bool FfmpegAvailable => Disponivel.Value;

    /// <summary>Cria um vídeo sintético e devolve o caminho do arquivo.</summary>
    public static async Task<string> CreateSampleAsync(
        string directory,
        double seconds = 6,
        int width = 1280,
        int height = 720,
        bool withAudio = true,
        int frameRate = 30,
        string fileName = "amostra.mp4")
    {
        Directory.CreateDirectory(directory);
        var caminho = Path.Combine(directory, fileName);

        var argumentos = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", $"testsrc=size={width}x{height}:rate={frameRate}:duration={seconds}"
        };

        if (withAudio)
            argumentos.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}", "-c:a", "aac", "-shortest"]);

        argumentos.AddRange(["-c:v", "libx264", "-pix_fmt", "yuv420p", "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), caminho]);

        var resultado = await ExecutarAsync("ffmpeg", argumentos);

        if (resultado.code != 0)
            throw new InvalidOperationException($"Não foi possível gerar o vídeo de amostra: {resultado.erro}");

        return caminho;
    }

    private static bool Existe(string programa)
    {
        try
        {
            var (code, _, _) = ExecutarAsync(programa, ["-version"]).GetAwaiter().GetResult();
            return code == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(int code, string saida, string erro)> ExecutarAsync(string programa, IReadOnlyList<string> argumentos)
    {
        var inicio = new ProcessStartInfo
        {
            FileName = programa,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argumento in argumentos)
            inicio.ArgumentList.Add(argumento);

        using var processo = Process.Start(inicio) ?? throw new InvalidOperationException($"Não foi possível iniciar {programa}.");

        var saida = processo.StandardOutput.ReadToEndAsync();
        var erro = processo.StandardError.ReadToEndAsync();

        await processo.WaitForExitAsync();

        return (processo.ExitCode, await saida, await erro);
    }
}

/// <summary>
/// Marca um teste que depende do FFmpeg instalado. Em uma máquina sem as ferramentas o teste
/// é ignorado com a razão à vista, em vez de falhar por motivo alheio ao código.
/// </summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (!MediaTools.FfmpegAvailable)
            Skip = "FFmpeg não está instalado nesta máquina.";
    }
}
