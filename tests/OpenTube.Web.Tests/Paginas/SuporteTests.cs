using System.Net;
using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Enums;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Paginas;

/// <summary>
/// Conversas de suporte pela interface: quem escreve, quem enxerga e como a administração
/// responde.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class SuporteTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private const string Admin = "allan@barcelos.dev";
    private const string Pessoa = "convidado@barcelos.dev";
    private const string Outra = "terceira@barcelos.dev";

    private OpenTubeWebFactory _app = default!;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        _app = new OpenTubeWebFactory(postgres, minio, Admin);

        using var cliente = _app.CreateBrowser();
        await cliente.GetAsync("/saude");
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task EntrarAsync(HttpClient cliente, string email)
    {
        await using (var db = postgres.CreateContext())
        {
            if (!await db.Users.AnyAsync(u => u.Email == email))
            {
                db.Users.Add(OpenTube.Domain.Entities.User.Create(
                    OpenTube.Domain.ValueObjects.EmailAddress.Parse(email), DateTimeOffset.UtcNow));
                await db.SaveChangesAsync();
            }
        }

        _app.Emails.Clear();

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/entrar", "/entrar/codigo", new Dictionary<string, string> { ["email"] = email });

        await FormularioHelpers.EnviarFormularioAsync(
            cliente,
            $"/entrar?email={Uri.EscapeDataString(email)}&enviado=1",
            "/entrar/verificar",
            new Dictionary<string, string> { ["email"] = email, ["codigo"] = _app.Emails.LastCode() });
    }

    private async Task<OpenTube.Domain.Entities.Video> CriarVideoAsync()
    {
        using var storage = minio.CreateStorage();

        return await AcervoDeTeste.PublicarAsync(postgres, storage, "Reunião Trimestral", VideoVisibility.Public);
    }

    private async Task AbrirConversaAsync(HttpClient cliente, OpenTube.Domain.Entities.Video video, string texto, string? instante = null)
    {
        var campos = new Dictionary<string, string>
        {
            ["videoId"] = video.Id.ToString(),
            ["mensagem"] = texto,
            ["destino"] = $"/v/{video.Slug}"
        };

        if (instante is not null)
            campos["instante"] = instante;

        await FormularioHelpers.EnviarFormularioAsync(cliente, $"/v/{video.Slug}", "/suporte/abrir", campos);
    }

    [Fact]
    public async Task Visitante_anonimo_e_convidado_a_entrar()
    {
        var video = await CriarVideoAsync();

        using var cliente = _app.CreateBrowser();
        var html = await cliente.GetStringAsync($"/v/{video.Slug}");

        Assert.Contains("Fale com a administração", html);
        Assert.Contains("para enviar uma mensagem sobre este vídeo", html);
    }

    [Fact]
    public async Task Quem_entrou_escreve_e_a_mensagem_aparece()
    {
        var video = await CriarVideoAsync();

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Pessoa);
        await AbrirConversaAsync(cliente, video, "Não consigo ouvir o áudio", "83");

        var html = await cliente.GetStringAsync($"/v/{video.Slug}");

        Assert.Contains("Não consigo ouvir o áudio", html);
        Assert.Contains("Aguardando resposta", html);
        Assert.Contains("sobre 1:23", html);
    }

    [Fact]
    public async Task A_conversa_de_uma_pessoa_nao_aparece_para_outra()
    {
        var video = await CriarVideoAsync();

        using var dona = _app.CreateBrowser();
        await EntrarAsync(dona, Pessoa);
        await AbrirConversaAsync(dona, video, "Assunto reservado");

        using var terceira = _app.CreateBrowser();
        await EntrarAsync(terceira, Outra);

        var html = await terceira.GetStringAsync($"/v/{video.Slug}");

        Assert.DoesNotContain("Assunto reservado", html);
    }

    [Fact]
    public async Task A_administracao_ve_a_conversa_na_fila_e_responde()
    {
        var video = await CriarVideoAsync();

        using var pessoa = _app.CreateBrowser();
        await EntrarAsync(pessoa, Pessoa);
        await AbrirConversaAsync(pessoa, video, "Não consigo ouvir o áudio");

        using var admin = _app.CreateBrowser();
        await EntrarAsync(admin, Admin);

        var fila = await admin.GetStringAsync("/admin/suporte");
        Assert.Contains(Pessoa, fila);
        Assert.Contains("Reunião Trimestral", fila);
        Assert.Contains("nova", fila);

        Guid conversaId;
        await using (var db = postgres.CreateContext())
            conversaId = (await db.SupportThreads.SingleAsync()).Id;

        await FormularioHelpers.EnviarFormularioAsync(
            admin, $"/admin/suporte/{conversaId}", $"/suporte/{conversaId}/responder",
            new Dictionary<string, string>
            {
                ["mensagem"] = "O áudio está no segundo canal",
                ["destino"] = $"/admin/suporte/{conversaId}"
            });

        // A resposta chega a quem perguntou, na página do vídeo.
        Assert.Contains("O áudio está no segundo canal", await pessoa.GetStringAsync($"/v/{video.Slug}"));
        Assert.Contains("Respondida", await pessoa.GetStringAsync($"/v/{video.Slug}"));
    }

    [Fact]
    public async Task Abrir_a_conversa_limpa_o_aviso_de_pendencia()
    {
        var video = await CriarVideoAsync();

        using var pessoa = _app.CreateBrowser();
        await EntrarAsync(pessoa, Pessoa);
        await AbrirConversaAsync(pessoa, video, "Pergunta");

        Guid conversaId;
        await using (var db = postgres.CreateContext())
            conversaId = (await db.SupportThreads.SingleAsync()).Id;

        using var admin = _app.CreateBrowser();
        await EntrarAsync(admin, Admin);

        Assert.Contains("nova", await admin.GetStringAsync("/admin/suporte"));

        await admin.GetStringAsync($"/admin/suporte/{conversaId}");

        Assert.DoesNotContain(">nova<", await admin.GetStringAsync("/admin/suporte"));
    }

    [Fact]
    public async Task Encerrar_e_reabrir_pela_interface()
    {
        var video = await CriarVideoAsync();

        using var pessoa = _app.CreateBrowser();
        await EntrarAsync(pessoa, Pessoa);
        await AbrirConversaAsync(pessoa, video, "Pergunta");

        Guid conversaId;
        await using (var db = postgres.CreateContext())
            conversaId = (await db.SupportThreads.SingleAsync()).Id;

        using var admin = _app.CreateBrowser();
        await EntrarAsync(admin, Admin);

        await FormularioHelpers.EnviarFormularioAsync(
            admin, $"/admin/suporte/{conversaId}", $"/admin/suporte/{conversaId}/encerrar", new Dictionary<string, string>());

        Assert.Contains("Esta conversa foi encerrada", await pessoa.GetStringAsync($"/v/{video.Slug}"));

        await FormularioHelpers.EnviarFormularioAsync(
            admin, $"/admin/suporte/{conversaId}", $"/admin/suporte/{conversaId}/reabrir", new Dictionary<string, string>());

        Assert.DoesNotContain("Esta conversa foi encerrada", await pessoa.GetStringAsync($"/v/{video.Slug}"));
    }

    [Fact]
    public async Task Uma_pessoa_nao_responde_na_conversa_de_outra()
    {
        var video = await CriarVideoAsync();

        using var dona = _app.CreateBrowser();
        await EntrarAsync(dona, Pessoa);
        await AbrirConversaAsync(dona, video, "Assunto reservado");

        Guid conversaId;
        await using (var db = postgres.CreateContext())
            conversaId = (await db.SupportThreads.SingleAsync()).Id;

        using var intrusa = _app.CreateBrowser();
        await EntrarAsync(intrusa, Outra);

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            intrusa, $"/v/{video.Slug}", $"/suporte/{conversaId}/responder",
            new Dictionary<string, string> { ["mensagem"] = "Intrusão", ["destino"] = $"/v/{video.Slug}" });

        Assert.Contains("erro=", resposta.Headers.Location!.ToString());

        await using var leitura = postgres.CreateContext();
        Assert.Equal(1, await leitura.SupportMessages.CountAsync());
    }

    [Fact]
    public async Task Convidado_nao_alcanca_a_fila_do_suporte()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Pessoa);

        Assert.NotEqual(HttpStatusCode.OK, (await cliente.GetAsync("/admin/suporte")).StatusCode);
    }

    [Fact]
    public async Task O_painel_mostra_quantas_conversas_aguardam()
    {
        var video = await CriarVideoAsync();

        using var pessoa = _app.CreateBrowser();
        await EntrarAsync(pessoa, Pessoa);
        await AbrirConversaAsync(pessoa, video, "Pergunta");

        using var admin = _app.CreateBrowser();
        await EntrarAsync(admin, Admin);

        var html = await admin.GetStringAsync("/admin");

        Assert.Contains("Suporte aguardando", html);
    }

    [Fact]
    public async Task O_destino_do_formulario_nao_leva_para_fora_do_site()
    {
        var video = await CriarVideoAsync();

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Pessoa);

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/v/{video.Slug}", "/suporte/abrir",
            new Dictionary<string, string>
            {
                ["videoId"] = video.Id.ToString(),
                ["mensagem"] = "Pergunta",
                ["destino"] = "https://exemplo-malicioso.com"
            });

        var destino = resposta.Headers.Location!.ToString();

        Assert.DoesNotContain("exemplo-malicioso", destino);
        Assert.StartsWith("/", destino);
    }

    [Fact]
    public async Task Conversa_inexistente_responde_como_nao_encontrada()
    {
        using var admin = _app.CreateBrowser();
        await EntrarAsync(admin, Admin);

        var resposta = await admin.GetAsync($"/admin/suporte/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }
}
