using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTube.Domain.Captions;
using OpenTube.Worker.Media;

namespace OpenTube.Worker.Tests.Media;

/// <summary>
/// O cliente do container do Whisper contra um servidor simulado: saúde, descrição do motor,
/// resposta em verbose_json virando WebVTT e o idioma detectado virando código.
/// </summary>
public class WhisperServerTranscriberTests : IDisposable
{
    private readonly string _pasta = Directory.CreateTempSubdirectory("opentube-whisper-http-").FullName;

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

    /// <summary>"Extrai" o áudio escrevendo o último argumento do FFmpeg, que é o arquivo de saída.</summary>
    private sealed class FfmpegFalso : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            await File.WriteAllBytesAsync(arguments[^1], [1, 2, 3], cancellationToken);
            return new ProcessResult(0, string.Empty, string.Empty);
        }
    }

    /// <summary>Servidor simulado: responde por caminho e guarda o formulário recebido.</summary>
    private sealed class ServidorFalso(Func<string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public string? Formulario { get; private set; }

        public List<string> Caminhos { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Caminhos.Add(request.RequestUri!.AbsolutePath);

            if (request.Content is not null)
                Formulario = await request.Content.ReadAsStringAsync(cancellationToken);

            return responder(request.RequestUri.AbsolutePath);
        }
    }

    private static HttpResponseMessage Json(string conteudo) =>
        new(HttpStatusCode.OK) { Content = new StringContent(conteudo, Encoding.UTF8, "application/json") };

    private static WhisperServerTranscriber Criar(HttpMessageHandler servidor, string? endereco = "http://whisper:8080/") =>
        new(new FfmpegFalso(),
            new WhisperHttp(servidor),
            Microsoft.Extensions.Options.Options.Create(new TranscriptionOptions { ServerUrl = endereco }),
            Microsoft.Extensions.Options.Options.Create(new MediaToolOptions()),
            NullLogger<WhisperServerTranscriber>.Instance);

    private const string Resposta = """
        {
          "task": "transcribe",
          "language": "portuguese",
          "duration": 4.2,
          "text": " Bom dia a todos. Sejam bem-vindos.",
          "segments": [
            { "id": 0, "start": 0.0, "end": 1.8, "text": " Bom dia a todos." },
            { "id": 1, "start": 1.8, "end": 1.8, "text": " Sejam bem-vindos." },
            { "id": 2, "start": 2.0, "end": 3.0, "text": "   " }
          ],
          "detected_language": "portuguese",
          "detected_language_probability": 0.97
        }
        """;

    [Fact]
    public void Sem_endereco_fica_desligado()
    {
        Assert.False(Criar(new ServidorFalso(_ => Json("{}")), endereco: null).IsAvailable);
        Assert.True(Criar(new ServidorFalso(_ => Json("{}"))).IsAvailable);
    }

    [Fact]
    public async Task Servidor_saudavel_informa_o_motor_escolhido_pelo_container()
    {
        var servidor = new ServidorFalso(caminho => caminho switch
        {
            "/health" => Json("""{"status":"ok"}"""),
            "/info.json" => Json("""{"acceleration":"cuda","model":"large-v3-turbo-q5_0","threads":4}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var situacao = await Criar(servidor).CheckAsync();

        Assert.True(situacao.Available);
        Assert.Equal("whisper.cpp · CUDA · large-v3-turbo-q5_0 · 4 threads", situacao.Engine);
    }

    [Fact]
    public async Task Servidor_sem_info_ainda_e_disponivel()
    {
        var servidor = new ServidorFalso(caminho => caminho == "/health"
            ? Json("""{"status":"ok"}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        var situacao = await Criar(servidor).CheckAsync();

        Assert.True(situacao.Available);
        Assert.Equal("whisper.cpp", situacao.Engine);
    }

    [Fact]
    public async Task Servidor_carregando_o_modelo_nao_esta_disponivel()
    {
        // O whisper-server responde 503 em /health enquanto carrega o modelo.
        var servidor = new ServidorFalso(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        Assert.False((await Criar(servidor).CheckAsync()).Available);
    }

    [Fact]
    public async Task Servidor_fora_do_ar_nao_esta_disponivel()
    {
        var servidor = new ServidorFalso(_ => throw new HttpRequestException("Connection refused"));

        Assert.False((await Criar(servidor).CheckAsync()).Available);
    }

    [Fact]
    public async Task Transcreve_detectando_o_idioma_e_monta_webvtt_valido()
    {
        var servidor = new ServidorFalso(_ => Json(Resposta));

        var resultado = await Criar(servidor).TranscribeAsync("video.mp4", _pasta, "auto");

        Assert.Equal("pt", resultado.Language);
        Assert.Contains("/inference", servidor.Caminhos);
        Assert.Contains("verbose_json", servidor.Formulario);
        Assert.Contains("auto", servidor.Formulario);

        var legenda = CaptionDocument.Parse(await File.ReadAllTextAsync(resultado.VttPath));

        // O trecho em branco some; o trecho sem duração ganha um instante mínimo.
        Assert.Equal(2, legenda.Cues.Count);
        Assert.Equal("Bom dia a todos.", legenda.Cues[0].Text);
        Assert.True(legenda.Cues[1].End > legenda.Cues[1].Start);
        Assert.Equal("Bom dia a todos. Sejam bem-vindos.", resultado.Text);
    }

    [Fact]
    public async Task Idioma_pedido_e_enviado_e_mantido()
    {
        var servidor = new ServidorFalso(_ => Json(Resposta.Replace("portuguese", "english")));

        var resultado = await Criar(servidor).TranscribeAsync("video.mp4", _pasta, "pt");

        Assert.Equal("pt", resultado.Language);
        Assert.DoesNotContain("auto", servidor.Formulario);
    }

    [Fact]
    public async Task Erro_do_servidor_vira_falha_da_transcricao()
    {
        var servidor = new ServidorFalso(_ => Json("""{"error":"failed to read audio"}"""));

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Criar(servidor).TranscribeAsync("video.mp4", _pasta, "auto"));

        Assert.Contains("failed to read audio", erro.Message);
    }

    [Theory]
    [InlineData("portuguese", "pt")]
    [InlineData("English", "en")]
    [InlineData("haitian creole", "ht")]
    [InlineData("pt", "pt")]
    [InlineData("klingon", null)]
    [InlineData(null, null)]
    public void Nome_do_idioma_vira_codigo(string? nome, string? codigo) =>
        Assert.Equal(codigo, WhisperLanguages.ToCode(nome));
}
