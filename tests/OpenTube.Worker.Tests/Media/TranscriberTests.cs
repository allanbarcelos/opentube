using Microsoft.Extensions.Logging.Abstractions;
using OpenTube.Worker.Media;

namespace OpenTube.Worker.Tests.Media;

/// <summary>
/// Montagem da linha de comando da transcrição. A forma dos argumentos fica em configuração
/// porque cada ferramenta tem a sua.
/// </summary>
public class TranscriberTests
{
    private static CommandLineTranscriber Criar(TranscriptionOptions opcoes) =>
        new(new ProcessRunner(NullLogger<ProcessRunner>.Instance),
            Microsoft.Extensions.Options.Options.Create(opcoes),
            Microsoft.Extensions.Options.Options.Create(new MediaToolOptions()),
            NullLogger<CommandLineTranscriber>.Instance);

    [Fact]
    public void Sem_programa_configurado_o_recurso_fica_desligado()
    {
        Assert.False(Criar(new TranscriptionOptions()).IsAvailable);
        Assert.True(Criar(new TranscriptionOptions { Executable = "whisper" }).IsAvailable);
    }

    [Fact]
    public async Task Recusa_transcrever_com_o_recurso_desligado()
    {
        var transcritor = Criar(new TranscriptionOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transcritor.TranscribeAsync("/tmp/a.mp4", "/tmp", "pt"));
    }

    [Fact]
    public void Substitui_os_marcadores_pelos_caminhos_reais()
    {
        var transcritor = Criar(new TranscriptionOptions
        {
            Executable = "whisper",
            Arguments = "-m {modelo} -f {entrada} -l {idioma} -ovtt -of {saida}",
            ModelPath = "/modelos/ggml-medium.bin",
            Language = "pt"
        });

        var argumentos = transcritor.MontarArgumentos("/trabalho/audio.wav", "/trabalho/legenda");

        Assert.Equal(
            ["-m", "/modelos/ggml-medium.bin", "-f", "/trabalho/audio.wav", "-l", "pt", "-ovtt", "-of", "/trabalho/legenda"],
            argumentos);
    }

    [Fact]
    public void O_idioma_do_pedido_substitui_o_da_configuracao()
    {
        var transcritor = Criar(new TranscriptionOptions
        {
            Executable = "whisper",
            Arguments = "-l {idioma} -f {entrada}",
            Language = "pt"
        });

        Assert.Equal(["-l", "en", "-f", "/a.wav"], transcritor.MontarArgumentos("/a.wav", "/b", "en"));
        Assert.Equal(["-l", "pt", "-f", "/a.wav"], transcritor.MontarArgumentos("/a.wav", "/b", null));
    }

    [Fact]
    public void Cada_argumento_vai_separado()
    {
        // Montar uma linha só exigiria escapar espaços na mão, e um caminho com espaço
        // viraria dois argumentos.
        var transcritor = Criar(new TranscriptionOptions { Executable = "whisper", Arguments = "-f {entrada}" });

        var argumentos = transcritor.MontarArgumentos("/com espaço/audio.wav", "/saida");

        Assert.Equal(["-f", "/com espaço/audio.wav"], argumentos);
    }

    [Fact]
    public void Modelo_ausente_nao_deixa_marcador_solto()
    {
        var transcritor = Criar(new TranscriptionOptions { Executable = "whisper", Arguments = "-m {modelo} -f {entrada}" });

        Assert.DoesNotContain("{modelo}", transcritor.MontarArgumentos("/a.wav", "/b"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Exige_os_caminhos(string caminho)
    {
        var transcritor = Criar(new TranscriptionOptions { Executable = "whisper" });

        await Assert.ThrowsAnyAsync<ArgumentException>(() => transcritor.TranscribeAsync(caminho, "/tmp", "pt"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => transcritor.TranscribeAsync("/tmp/a.mp4", caminho, "pt"));
    }
}
