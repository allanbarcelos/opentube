using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OpenTube.Worker.Media;

/// <summary>Resultado da execução de um programa externo.</summary>
/// <param name="ExitCode">Código de saída.</param>
/// <param name="StandardOutput">Saída padrão.</param>
/// <param name="StandardError">Saída de erro.</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>Mensagem curta para registro, já que o FFmpeg pode ser bastante prolixo.</summary>
    public string ShortError()
    {
        var texto = string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError;
        texto = texto.Trim();

        return texto.Length <= 1000 ? texto : texto[^1000..];
    }
}

/// <summary>Execução de programas externos, isolada para permitir substituição nos testes.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

public class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var inicio = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Cada argumento entra separado: montar uma linha só exigiria escapar aspas e
        // espaços na mão, e um nome de arquivo estranho viraria injeção de comando.
        foreach (var argumento in arguments)
            inicio.ArgumentList.Add(argumento);

        using var processo = new Process { StartInfo = inicio };

        var saida = new StringBuilder();
        var erro = new StringBuilder();

        processo.OutputDataReceived += (_, e) => { if (e.Data is not null) saida.AppendLine(e.Data); };
        processo.ErrorDataReceived += (_, e) => { if (e.Data is not null) erro.AppendLine(e.Data); };

        logger.LogDebug("Executando {Programa} {Argumentos}", fileName, string.Join(' ', arguments));

        processo.Start();
        processo.BeginOutputReadLine();
        processo.BeginErrorReadLine();

        try
        {
            await processo.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TentarEncerrar(processo);
            throw;
        }

        return new ProcessResult(processo.ExitCode, saida.ToString(), erro.ToString());
    }

    private void TentarEncerrar(Process processo)
    {
        try
        {
            if (!processo.HasExited)
                processo.Kill(entireProcessTree: true);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Não foi possível encerrar o processo cancelado");
        }
    }
}
