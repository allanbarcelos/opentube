using System.Net;
using OpenTube.Domain.Enums;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Paginas;

/// <summary>
/// Home, busca e página do vídeo. O que importa aqui é o que aparece e, sobretudo, o que
/// não aparece para quem não tem acesso.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class CatalogoTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private const string Admin = "allan@barcelos.dev";

    private OpenTubeWebFactory _app = default!;
    private IDisposable _storage = default!;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        _app = new OpenTubeWebFactory(postgres, minio, Admin);

        using var cliente = _app.CreateBrowser();
        await cliente.GetAsync("/saude");
    }

    public async Task DisposeAsync()
    {
        _storage?.Dispose();
        await _app.DisposeAsync();
    }

    private async Task EntrarComoAdminAsync(HttpClient cliente)
    {
        _app.Emails.Clear();

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/entrar", "/entrar/codigo", new Dictionary<string, string> { ["email"] = Admin });

        await FormularioHelpers.EnviarFormularioAsync(
            cliente,
            $"/entrar?email={Uri.EscapeDataString(Admin)}&enviado=1",
            "/entrar/verificar",
            new Dictionary<string, string> { ["email"] = Admin, ["codigo"] = _app.Emails.LastCode() });
    }

    [Fact]
    public async Task A_home_mostra_os_videos_publicos_a_quem_nao_entrou()
    {
        using var storage = minio.CreateStorage();
        var publico = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var html = await cliente.GetStringAsync("/");

        Assert.Contains("Boas-vindas", html);
        Assert.Contains($"/v/{publico.Slug}", html);
    }

    [Fact]
    public async Task A_home_nao_mostra_video_privado_nem_restrito()
    {
        using var storage = minio.CreateStorage();
        await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Private);
        await AcervoDeTeste.PublicarAsync(postgres, storage, "Somente Convidados", VideoVisibility.Restricted);

        using var cliente = _app.CreateBrowser();
        var html = await cliente.GetStringAsync("/");

        Assert.DoesNotContain("Plano Confidencial", html);
        Assert.DoesNotContain("Somente Convidados", html);
        Assert.Contains("Nenhum vídeo público disponível", html);
    }

    [Fact]
    public async Task O_administrador_ve_o_acervo_inteiro_com_a_visibilidade_a_vista()
    {
        using var storage = minio.CreateStorage();
        await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Private);
        await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var html = await cliente.GetStringAsync("/");

        Assert.Contains("Plano Confidencial", html);
        Assert.Contains("Boas-vindas", html);
        Assert.Contains("Privado", html);
        Assert.Contains("Público", html);
    }

    [Fact]
    public async Task A_busca_encontra_pelo_titulo_ignorando_acentos()
    {
        using var storage = minio.CreateStorage();
        await AcervoDeTeste.PublicarAsync(postgres, storage, "Reunião de Orçamento", VideoVisibility.Public);
        await AcervoDeTeste.PublicarAsync(postgres, storage, "Treinamento de Segurança", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var html = await cliente.GetStringAsync("/?q=orcamento");

        Assert.Contains("Reunião de Orçamento", html);
        Assert.DoesNotContain("Treinamento de Segurança", html);
    }

    [Fact]
    public async Task A_busca_encontra_pela_descricao_e_pelas_etiquetas()
    {
        using var storage = minio.CreateStorage();
        await AcervoDeTeste.PublicarAsync(postgres, storage, "Encontro mensal", VideoVisibility.Public,
            descricao: "falamos sobre contratação de fornecedores");
        await AcervoDeTeste.PublicarAsync(postgres, storage, "Outro assunto", VideoVisibility.Public,
            etiquetas: ["compliance"]);

        using var cliente = _app.CreateBrowser();

        Assert.Contains("Encontro mensal", await cliente.GetStringAsync("/?q=fornecedores"));
        Assert.Contains("Outro assunto", await cliente.GetStringAsync("/?q=compliance"));
    }

    [Fact]
    public async Task A_busca_nao_alcanca_video_sem_acesso()
    {
        using var storage = minio.CreateStorage();
        await AcervoDeTeste.PublicarAsync(postgres, storage, "Segredo Industrial", VideoVisibility.Private);

        using var cliente = _app.CreateBrowser();
        var html = await cliente.GetStringAsync("/?q=segredo");

        Assert.DoesNotContain("Segredo Industrial", html);
        Assert.Contains("Nenhum vídeo corresponde", html);
    }

    [Fact]
    public async Task A_pagina_do_video_publico_traz_o_player()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public,
            descricao: "Apresentação da plataforma");

        using var cliente = _app.CreateBrowser();
        var html = await cliente.GetStringAsync($"/v/{video.Slug}");

        Assert.Contains("Boas-vindas", html);
        Assert.Contains("Apresentação da plataforma", html);
        Assert.Contains($"/api/videos/{video.Id}/master.m3u8", html);
        Assert.Contains("2:05", html);
    }

    [Fact]
    public async Task A_pagina_de_um_video_sem_acesso_responde_como_inexistente()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Private);

        using var cliente = _app.CreateBrowser();
        var resposta = await cliente.GetAsync($"/v/{video.Slug}");
        var html = await resposta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Contains("Vídeo não encontrado", html);
        // Nem o título pode vazar: saber que existe já é informação sobre o acervo.
        Assert.DoesNotContain("Plano Confidencial", html);
    }

    [Fact]
    public async Task Endereco_inventado_responde_como_inexistente()
    {
        using var cliente = _app.CreateBrowser();

        var resposta = await cliente.GetAsync("/v/nao-existe");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }
}
