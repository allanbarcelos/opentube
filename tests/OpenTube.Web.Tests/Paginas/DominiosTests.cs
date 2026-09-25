using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Access;
using OpenTube.Infrastructure.Domains;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Paginas;

/// <summary>
/// Cadastro, verificação por DNS e porta de entrada de um domínio, do ponto de vista de quem
/// administra e de quem chega pela porta.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class DominiosTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private const string Admin = "allan@barcelos.dev";

    private OpenTubeWebFactory _app = default!;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        _app = new OpenTubeWebFactory(postgres, minio, Admin);

        using var cliente = _app.CreateBrowser();
        await cliente.GetAsync("/saude");
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

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

    private async Task<Guid> CadastrarAsync(HttpClient cliente, string dominio, string? responsavel = null)
    {
        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/admin/dominios", "/admin/dominios/cadastrar",
            new Dictionary<string, string>
            {
                ["dominio"] = dominio,
                ["responsavel"] = responsavel ?? "",
                ["nota"] = ""
            });

        var destino = resposta.Headers.Location!.ToString();

        return Guid.Parse(destino.Split('/')[^1].Split('?')[0]);
    }

    private async Task PublicarRegistroAsync(Guid domainId)
    {
        using var escopo = _app.Services.CreateScope();
        var dominio = await escopo.ServiceProvider.GetRequiredService<DomainService>().FindAsync(domainId);

        _app.Dns.Publicar(dominio!.VerificationRecordName, dominio.ExpectedRecordValue);
    }

    private async Task ConcederAoDominioAsync(string nome)
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Restricted);

        using var escopo = _app.Services.CreateScope();
        await escopo.ServiceProvider.GetRequiredService<GrantService>().GrantToDomainAsync(
            nome, GrantTargetType.Video, video.Id, GrantValidity.Forever, Guid.CreateVersion7());
    }

    [Fact]
    public async Task A_pagina_do_dominio_mostra_o_registro_a_publicar()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        var id = await CadastrarAsync(cliente, "barcelos.dev");

        var html = await cliente.GetStringAsync($"/admin/dominios/{id}");

        Assert.Contains("_opentube-verify.barcelos.dev", html);
        Assert.Contains("opentube-verify=", html);
        Assert.Contains("Já publiquei, verificar", html);
    }

    [Fact]
    public async Task A_porta_nao_abre_antes_da_verificacao()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        await CadastrarAsync(cliente, "barcelos.dev");

        using var visitante = _app.CreateBrowser();

        Assert.Equal(HttpStatusCode.NotFound, (await visitante.GetAsync("/d/barcelos.dev")).StatusCode);
    }

    [Fact]
    public async Task Verificar_sem_o_registro_publicado_avisa_o_administrador()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        var id = await CadastrarAsync(cliente, "barcelos.dev");

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/dominios/{id}", $"/admin/dominios/{id}/verificar", new Dictionary<string, string>());

        Assert.Contains("falhou=1", resposta.Headers.Location!.ToString());
        Assert.Contains("ainda não foi encontrado", await cliente.GetStringAsync($"/admin/dominios/{id}?falhou=1"));
    }

    [Fact]
    public async Task Verificar_com_o_registro_publicado_abre_a_porta()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        var id = await CadastrarAsync(cliente, "barcelos.dev");
        await PublicarRegistroAsync(id);

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/dominios/{id}", $"/admin/dominios/{id}/verificar", new Dictionary<string, string>());

        using var visitante = _app.CreateBrowser();
        var pagina = await visitante.GetAsync("/d/barcelos.dev");

        Assert.Equal(HttpStatusCode.OK, pagina.StatusCode);
        Assert.Contains("Acesso de barcelos.dev", await pagina.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_porta_recusa_email_de_outro_dominio()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        var id = await CadastrarAsync(cliente, "barcelos.dev");
        await PublicarRegistroAsync(id);
        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/dominios/{id}", $"/admin/dominios/{id}/verificar", new Dictionary<string, string>());

        using var visitante = _app.CreateBrowser();
        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            visitante, "/d/barcelos.dev", "/d/barcelos.dev/codigo",
            new Dictionary<string, string> { ["email"] = "alguem@outra.com" });

        Assert.Contains("erro=", resposta.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Quem_e_do_dominio_entra_pela_porta_e_assiste()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        var id = await CadastrarAsync(cliente, "empresa.com");
        await PublicarRegistroAsync(id);
        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/dominios/{id}", $"/admin/dominios/{id}/verificar", new Dictionary<string, string>());

        await ConcederAoDominioAsync("empresa.com");

        using var visitante = _app.CreateBrowser();
        _app.Emails.Clear();

        await FormularioHelpers.EnviarFormularioAsync(
            visitante, "/d/empresa.com", "/d/empresa.com/codigo",
            new Dictionary<string, string> { ["email"] = "pessoa@empresa.com" });

        Assert.Single(_app.Emails.Sent);

        await FormularioHelpers.EnviarFormularioAsync(
            visitante,
            "/d/empresa.com?email=pessoa%40empresa.com&enviado=1",
            "/entrar/verificar",
            new Dictionary<string, string>
            {
                ["email"] = "pessoa@empresa.com",
                ["codigo"] = _app.Emails.LastCode()
            });

        Assert.Contains("Plano Confidencial", await visitante.GetStringAsync("/"));
    }

    [Fact]
    public async Task Trocar_o_endereco_da_porta_desliga_o_anterior()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        var id = await CadastrarAsync(cliente, "barcelos.dev");
        await PublicarRegistroAsync(id);
        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/dominios/{id}", $"/admin/dominios/{id}/verificar", new Dictionary<string, string>());

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/dominios/{id}", $"/admin/dominios/{id}/salvar",
            new Dictionary<string, string>
            {
                ["endereco"] = "x7k2-privado",
                ["portaAtiva"] = "on",
                ["permitidos"] = "",
                ["responsavel"] = "",
                ["nota"] = ""
            });

        using var visitante = _app.CreateBrowser();

        Assert.Equal(HttpStatusCode.NotFound, (await visitante.GetAsync("/d/barcelos.dev")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await visitante.GetAsync("/d/x7k2-privado")).StatusCode);
    }

    [Fact]
    public async Task Fechar_a_porta_derruba_a_entrada_sem_perder_a_verificacao()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        var id = await CadastrarAsync(cliente, "barcelos.dev");
        await PublicarRegistroAsync(id);
        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/dominios/{id}", $"/admin/dominios/{id}/verificar", new Dictionary<string, string>());

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/dominios/{id}", $"/admin/dominios/{id}/salvar",
            new Dictionary<string, string>
            {
                ["endereco"] = "barcelos.dev",
                ["permitidos"] = "",
                ["responsavel"] = "",
                ["nota"] = ""
            });

        using var visitante = _app.CreateBrowser();
        Assert.Equal(HttpStatusCode.NotFound, (await visitante.GetAsync("/d/barcelos.dev")).StatusCode);

        await using var db = postgres.CreateContext();
        Assert.True((await db.VerifiedDomains.SingleAsync()).IsVerified);
    }

    [Fact]
    public async Task Envia_ao_responsavel_o_endereco_da_porta()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        var id = await CadastrarAsync(cliente, "barcelos.dev", "ti@barcelos.dev");
        await PublicarRegistroAsync(id);
        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/dominios/{id}", $"/admin/dominios/{id}/verificar", new Dictionary<string, string>());

        _app.Emails.Clear();

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/dominios/{id}", $"/admin/dominios/{id}/enviar-link", new Dictionary<string, string>());

        Assert.Contains("linkEnviado=1", resposta.Headers.Location!.ToString());
        Assert.Contains("/d/barcelos.dev", _app.Emails.Last!.TextBody);
    }

    [Fact]
    public async Task Cadastrar_nome_invalido_mostra_o_erro()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/admin/dominios", "/admin/dominios/cadastrar",
            new Dictionary<string, string> { ["dominio"] = "nao-e-dominio", ["responsavel"] = "", ["nota"] = "" });

        Assert.Contains("erro=", resposta.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Visitante_nao_administra_dominios()
    {
        using var cliente = _app.CreateBrowser();

        var resposta = await cliente.GetAsync("/admin/dominios");

        Assert.Equal(HttpStatusCode.Found, resposta.StatusCode);
        Assert.Contains("/entrar", resposta.Headers.Location!.ToString());
    }
}
