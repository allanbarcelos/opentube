// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.TestSupport;

/// <summary>
/// O Whisper instalado por 'make whisper': o whisper-cli no PATH e o modelo em
/// .whisper/modelo.bin, na raiz do repositório (ou no caminho de OPENTUBE_WHISPER_MODEL).
/// </summary>
public static class WhisperLocal
{
    public static string? Executavel { get; } = Procurar("whisper-cli");

    public static string? Modelo { get; } = ProcurarModelo();

    /// <summary>O servidor HTTP do whisper.cpp, que é o que o container de produção roda.</summary>
    public static string? Servidor { get; } = Procurar("whisper-server");

    /// <summary>O <c>say</c> do macOS, usado para gerar uma fala de verdade no teste.</summary>
    public static string? Fala { get; } = Procurar("say");

    public static bool Disponivel => Executavel is not null && Modelo is not null;

    private static string? Procurar(string programa) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(pasta => Path.Combine(pasta, programa))
            .FirstOrDefault(File.Exists);

    private static string? ProcurarModelo()
    {
        var configurado = Environment.GetEnvironmentVariable("OPENTUBE_WHISPER_MODEL");
        if (!string.IsNullOrWhiteSpace(configurado))
            return File.Exists(configurado) ? configurado : null;

        // Sobe a partir da pasta do teste até achar a raiz do repositório.
        for (var pasta = new DirectoryInfo(AppContext.BaseDirectory); pasta is not null; pasta = pasta.Parent)
        {
            var modelo = Path.Combine(pasta.FullName, ".whisper", "modelo.bin");
            if (File.Exists(modelo))
                return modelo;
        }

        return null;
    }
}

/// <summary>
/// Marca um teste que transcreve fala de verdade. Sem o Whisper (ou sem o <c>say</c> para
/// gerar a fala), o teste é ignorado com a razão à vista.
/// </summary>
public sealed class WhisperFactAttribute : FactAttribute
{
    public WhisperFactAttribute()
    {
        if (!WhisperLocal.Disponivel)
            Skip = "Whisper não está instalado nesta máquina (make whisper).";
        else if (WhisperLocal.Fala is null)
            Skip = "Sem o 'say' do macOS para gerar a fala do teste.";
        else if (!MediaTools.FfmpegAvailable)
            Skip = "FFmpeg não está instalado nesta máquina.";
    }
}

/// <summary>Como <see cref="WhisperFactAttribute"/>, exigindo também o whisper-server.</summary>
public sealed class WhisperServerFactAttribute : FactAttribute
{
    public WhisperServerFactAttribute()
    {
        var comum = new WhisperFactAttribute();

        if (comum.Skip is not null)
            Skip = comum.Skip;
        else if (WhisperLocal.Servidor is null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENTUBE_WHISPER_URL")))
            Skip = "O whisper-server não está instalado nesta máquina (make whisper).";
    }
}
