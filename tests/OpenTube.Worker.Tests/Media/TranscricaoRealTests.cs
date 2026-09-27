// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTube.Domain.Captions;
using OpenTube.TestSupport;
using OpenTube.Worker.Media;

namespace OpenTube.Worker.Tests.Media;

/// <summary>
/// Transcrição com o Whisper de verdade, como o worker do 'make watch' faz: fala gerada pelo
/// sistema, extração do áudio com o FFmpeg e legenda lida pelo mesmo leitor do editor.
/// </summary>
public class TranscricaoRealTests : IDisposable
{
    private readonly string _pasta = Directory.CreateTempSubdirectory("opentube-whisper-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_pasta, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CommandLineTranscriber Transcritor() =>
        new(new ProcessRunner(NullLogger<ProcessRunner>.Instance),
            Microsoft.Extensions.Options.Options.Create(new TranscriptionOptions
            {
                Executable = WhisperLocal.Executavel,
                ModelPath = WhisperLocal.Modelo
            }),
            Microsoft.Extensions.Options.Options.Create(new MediaToolOptions()),
            NullLogger<CommandLineTranscriber>.Instance);

    private async Task<string> FalarAsync(string voz, string frase)
    {
        var arquivo = Path.Combine(_pasta, $"{voz}.aiff");

        using var processo = Process.Start(WhisperLocal.Fala!, ["-v", voz, "-o", arquivo, frase])!;
        await processo.WaitForExitAsync();

        Assert.Equal(0, processo.ExitCode);
        return arquivo;
    }

    [WhisperFact]
    public async Task Transcreve_fala_em_portugues()
    {
        var fala = await FalarAsync("Luciana", "Bom dia a todos. Sejam bem-vindos à reunião de planejamento.");
        var trabalho = Directory.CreateDirectory(Path.Combine(_pasta, "pt")).FullName;

        var resultado = await Transcritor().TranscribeAsync(fala, trabalho, "pt");

        var legenda = CaptionDocument.Parse(await File.ReadAllTextAsync(resultado.VttPath));

        Assert.Equal("pt", resultado.Language);
        Assert.NotEmpty(legenda.Cues);
        Assert.Contains("bom dia a todos", legenda.PlainText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("planejamento", legenda.PlainText, StringComparison.OrdinalIgnoreCase);
    }

    [WhisperFact]
    public async Task Transcreve_no_idioma_pedido()
    {
        var fala = await FalarAsync("Samantha", "Good morning everyone. Welcome to the planning meeting.");
        var trabalho = Directory.CreateDirectory(Path.Combine(_pasta, "en")).FullName;

        var resultado = await Transcritor().TranscribeAsync(fala, trabalho, "en");

        var legenda = CaptionDocument.Parse(await File.ReadAllTextAsync(resultado.VttPath));

        Assert.Contains("good morning", legenda.PlainText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("planning meeting", legenda.PlainText, StringComparison.OrdinalIgnoreCase);
    }

    [WhisperFact]
    public async Task Linha_de_comando_detecta_o_idioma_falado()
    {
        var fala = await FalarAsync("Samantha", "Good morning everyone. Welcome to the planning meeting.");
        var trabalho = Directory.CreateDirectory(Path.Combine(_pasta, "auto")).FullName;

        var resultado = await Transcritor().TranscribeAsync(fala, trabalho, "auto");

        Assert.Equal("en", resultado.Language);
    }
}

/// <summary>
/// O whisper-server de verdade, na mesma forma do container de produção, numa porta livre e
/// com o modelo do 'make whisper'. Sobe uma vez para a classe inteira.
/// </summary>
public sealed class WhisperServerLocal : IAsyncLifetime
{
    private Process? _processo;

    public string Endereco { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        // Um servidor já no ar (o container da imagem, por exemplo) dispensa subir o local.
        var externo = Environment.GetEnvironmentVariable("OPENTUBE_WHISPER_URL");
        if (!string.IsNullOrWhiteSpace(externo))
        {
            Endereco = externo.TrimEnd('/');
            return;
        }

        if (WhisperLocal.Servidor is null || WhisperLocal.Modelo is null)
            return;

        var escuta = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        escuta.Start();
        var porta = ((System.Net.IPEndPoint)escuta.LocalEndpoint).Port;
        escuta.Stop();

        Endereco = $"http://127.0.0.1:{porta}";

        _processo = Process.Start(new ProcessStartInfo(WhisperLocal.Servidor,
            ["--host", "127.0.0.1", "--port", porta.ToString(System.Globalization.CultureInfo.InvariantCulture),
             "-m", WhisperLocal.Modelo, "-l", "auto"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        _processo.OutputDataReceived += (_, _) => { };
        _processo.ErrorDataReceived += (_, _) => { };
        _processo.BeginOutputReadLine();
        _processo.BeginErrorReadLine();

        using var cliente = new HttpClient();
        var limite = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < limite)
        {
            try
            {
                if ((await cliente.GetAsync($"{Endereco}/health")).IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("O whisper-server não ficou pronto em 60 segundos.");
    }

    public Task DisposeAsync()
    {
        if (_processo is { HasExited: false })
            _processo.Kill(entireProcessTree: true);

        _processo?.Dispose();
        return Task.CompletedTask;
    }
}

public class TranscricaoPeloServidorTests(WhisperServerLocal servidor) : IClassFixture<WhisperServerLocal>, IDisposable
{
    private readonly string _pasta = Directory.CreateTempSubdirectory("opentube-whisper-server-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_pasta, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private WhisperServerTranscriber Transcritor(WhisperHttp http) =>
        new(new ProcessRunner(NullLogger<ProcessRunner>.Instance),
            http,
            Microsoft.Extensions.Options.Options.Create(new TranscriptionOptions { ServerUrl = servidor.Endereco }),
            Microsoft.Extensions.Options.Options.Create(new MediaToolOptions()),
            NullLogger<WhisperServerTranscriber>.Instance);

    private async Task<string> FalarAsync(string voz, string frase)
    {
        var arquivo = Path.Combine(_pasta, $"{voz}.aiff");

        using var processo = Process.Start(WhisperLocal.Fala!, ["-v", voz, "-o", arquivo, frase])!;
        await processo.WaitForExitAsync();

        Assert.Equal(0, processo.ExitCode);
        return arquivo;
    }

    [WhisperServerFact]
    public async Task Servidor_responde_e_detecta_portugues_e_ingles()
    {
        using var http = new WhisperHttp();
        var transcritor = Transcritor(http);

        Assert.True((await transcritor.CheckAsync()).Available);

        var pt = await transcritor.TranscribeAsync(
            await FalarAsync("Luciana", "Bom dia a todos. Sejam bem-vindos à reunião de planejamento."),
            Directory.CreateDirectory(Path.Combine(_pasta, "pt")).FullName, "auto");

        var en = await transcritor.TranscribeAsync(
            await FalarAsync("Samantha", "Good morning everyone. Welcome to the planning meeting."),
            Directory.CreateDirectory(Path.Combine(_pasta, "en")).FullName, "auto");

        Assert.Equal("pt", pt.Language);
        Assert.Contains("bom dia a todos", pt.Text, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("en", en.Language);
        Assert.Contains("good morning", en.Text, StringComparison.OrdinalIgnoreCase);

        CaptionDocument.Parse(await File.ReadAllTextAsync(en.VttPath));
    }
}
