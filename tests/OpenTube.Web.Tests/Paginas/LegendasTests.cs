using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Storage;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Paginas;

/// <summary>Legendas: envio pela administração, entrega e regra de acesso.</summary>
[Collection(IntegrationCollection.Name)]
public class LegendasTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private const string Admin = "allan@barcelos.dev";

    private const string Vtt = """
        WEBVTT

        00:00:01.000 --> 00:00:04.000
        Bom dia a todos.
        """;

    private OpenTubeWebFactory _app = default!;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        _app = new OpenTubeWebFactory(postgres, minio, Admin);

        using var cliente = _app.CreateBrowser();
        await cliente.GetAsync("/health");
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task EntrarComoAdminAsync(HttpClient cliente)
    {
        _app.Emails.Clear();

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/sign-in", "/sign-in/code", new Dictionary<string, string> { ["email"] = Admin });

        await FormularioHelpers.EnviarFormularioAsync(
            cliente,
            $"/sign-in?email={Uri.EscapeDataString(Admin)}&enviado=1",
            "/sign-in/verify",
            new Dictionary<string, string> { ["email"] = Admin, ["codigo"] = _app.Emails.LastCode() });
    }

    private static async Task<HttpResponseMessage> EnviarLegendaAsync(
        HttpClient cliente, Guid videoId, string conteudo, string idioma = "pt-BR")
    {
        var token = await FormularioHelpers.TokenAntifalsificacaoAsync(cliente, $"/admin/videos/{videoId}");

        using var formulario = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent(idioma), "idioma" },
            { new StringContent("Português"), "rotulo" }
        };

        var arquivo = new ByteArrayContent(Encoding.UTF8.GetBytes(conteudo));
        arquivo.Headers.ContentType = new MediaTypeHeaderValue("text/vtt");
        formulario.Add(arquivo, "arquivo", "legenda.vtt");

        return await cliente.PostAsync($"/admin/videos/{videoId}/captions", formulario);
    }

    [Fact]
    public async Task Envia_uma_legenda_e_ela_aparece_no_player()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var resposta = await EnviarLegendaAsync(cliente, video.Id, Vtt);

        Assert.Equal(HttpStatusCode.Found, resposta.StatusCode);
        Assert.Contains("legenda=enviada", resposta.Headers.Location!.ToString());

        var pagina = await cliente.GetStringAsync($"/watch/{video.Slug}");

        Assert.Contains("<track", pagina);
        Assert.Contains("srclang=\"pt-br\"", pagina);
        Assert.Contains("Português", pagina);
    }

    [Fact]
    public async Task A_legenda_e_entregue_a_quem_tem_acesso_ao_video()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        await EnviarLegendaAsync(cliente, video.Id, Vtt);

        Guid legendaId;
        await using (var db = postgres.CreateContext())
            legendaId = (await db.VideoAssets.SingleAsync()).Id;

        using var visitante = _app.CreateBrowser();
        var resposta = await visitante.GetAsync($"/api/videos/{video.Id}/captions/{legendaId}.vtt");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("text/vtt", resposta.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Bom dia a todos", await resposta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_legenda_de_video_sem_acesso_nao_e_entregue()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Private);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        await EnviarLegendaAsync(cliente, video.Id, Vtt);

        Guid legendaId;
        await using (var db = postgres.CreateContext())
            legendaId = (await db.VideoAssets.SingleAsync()).Id;

        using var visitante = _app.CreateBrowser();

        // O texto falado costuma revelar tanto quanto a imagem.
        Assert.Equal(HttpStatusCode.NotFound,
            (await visitante.GetAsync($"/api/videos/{video.Id}/captions/{legendaId}.vtt")).StatusCode);
    }

    [Fact]
    public async Task Arquivo_srt_e_aceito_e_guardado_como_webvtt()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var resposta = await EnviarLegendaAsync(cliente, video.Id, "1\n00:00:01,000 --> 00:00:02,000\nfala");

        Assert.Contains("legenda=enviada", resposta.Headers.Location!.ToString());

        await using var db = postgres.CreateContext();
        var legenda = await db.VideoAssets.SingleAsync();
        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nfala\n",
            await storage.GetTextAsync(StorageBucket.Vod, legenda.StorageKey));
    }

    [Fact]
    public async Task Arquivo_que_nao_e_legenda_e_recusado_com_o_motivo()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var resposta = await EnviarLegendaAsync(cliente, video.Id, "isto não é uma legenda");
        var destino = resposta.Headers.Location!.ToString();

        Assert.Contains("tab=captions", destino);
        Assert.Contains("The file must be WebVTT", await cliente.GetStringAsync(destino));

        await using var db = postgres.CreateContext();
        Assert.Empty(await db.VideoAssets.ToListAsync());
    }

    [Fact]
    public async Task Remove_uma_legenda()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        await EnviarLegendaAsync(cliente, video.Id, Vtt);

        Guid legendaId;
        await using (var db = postgres.CreateContext())
            legendaId = (await db.VideoAssets.SingleAsync()).Id;

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", $"/admin/videos/{video.Id}/captions/{legendaId}/delete",
            new Dictionary<string, string>());

        await using var leitura = postgres.CreateContext();
        Assert.Empty(await leitura.VideoAssets.ToListAsync());
    }

    [Fact]
    public async Task Pedir_a_transcricao_coloca_o_trabalho_na_fila()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        await storage.PutTextAsync(StorageBucket.Originals, video.OriginalKey, "arquivo", MediaTypes.Mp4);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}?tab=captions", $"/admin/videos/{video.Id}/captions/transcribe",
            new Dictionary<string, string> { ["idioma"] = "pt-BR" });

        Assert.Contains("legenda=pedida", resposta.Headers.Location!.ToString());

        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.ProcessingJobs.CountAsync(j => j.Kind == JobKind.Transcript));
    }

    [Fact]
    public async Task Sem_o_original_a_transcricao_e_recusada_com_o_motivo()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}?tab=captions", $"/admin/videos/{video.Id}/captions/transcribe",
            new Dictionary<string, string> { ["idioma"] = "pt-BR" });

        Assert.Contains("erro=", resposta.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Convidado_nao_envia_legenda()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();

        using var formulario = new MultipartFormDataContent
        {
            { new StringContent("pt-BR"), "idioma" }
        };
        formulario.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(Vtt)), "arquivo", "legenda.vtt");

        var resposta = await cliente.PostAsync($"/admin/videos/{video.Id}/captions", formulario);

        Assert.NotEqual(HttpStatusCode.OK, resposta.StatusCode);

        await using var db = postgres.CreateContext();
        Assert.Empty(await db.VideoAssets.ToListAsync());
    }
}
