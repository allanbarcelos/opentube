using System.Net;
using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Enums;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Paginas;

/// <summary>
/// Concessão de acesso pela interface administrativa: convite, domínio, link secreto e
/// revogação, com o efeito conferido do lado de quem assiste.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AcessosTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private const string Admin = "allan@barcelos.dev";
    private const string Convidado = "convidado@empresa.com";

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
        _app.Emails.Clear();

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/entrar", "/entrar/codigo", new Dictionary<string, string> { ["email"] = email });

        await FormularioHelpers.EnviarFormularioAsync(
            cliente,
            $"/entrar?email={Uri.EscapeDataString(email)}&enviado=1",
            "/entrar/verificar",
            new Dictionary<string, string> { ["email"] = email, ["codigo"] = _app.Emails.LastCode() });
    }

    private async Task<(HttpClient Cliente, OpenTube.Domain.Entities.Video Video)> PrepararAsync()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Restricted);

        var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        return (cliente, video);
    }

    [Fact]
    public async Task Convidar_pelo_formulario_libera_o_video_para_a_pessoa()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;
        _app.Emails.Clear();

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", "/admin/acessos/convidar",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString(),
                ["emails"] = Convidado,
                ["validade"] = "sempre",
                ["valorDaValidade"] = "",
                ["nota"] = "auditoria externa"
            });

        Assert.Contains("convidados=1", resposta.Headers.Location!.ToString());
        Assert.Single(_app.Emails.Sent);

        using var convidado = _app.CreateBrowser();
        await convidado.GetAsync($"/entrar/{_app.Emails.LastToken()}");

        Assert.Equal(HttpStatusCode.OK, (await convidado.GetAsync($"/v/{video.Slug}")).StatusCode);
    }

    [Fact]
    public async Task A_pagina_do_video_lista_quem_tem_acesso()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", "/admin/acessos/convidar",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString(),
                ["emails"] = Convidado,
                ["validade"] = "sempre",
                ["valorDaValidade"] = "",
                ["nota"] = "auditoria externa"
            });

        var html = await cliente.GetStringAsync($"/admin/videos/{video.Id}");

        Assert.Contains(Convidado, html);
        Assert.Contains("auditoria externa", html);
        Assert.Contains("sem prazo", html);
    }

    [Fact]
    public async Task Convite_com_prazo_relativo_aparece_descrito()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", "/admin/acessos/convidar",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString(),
                ["emails"] = Convidado,
                ["validade"] = "dias",
                ["valorDaValidade"] = "30"
            });

        Assert.Contains("30 dias a partir do primeiro acesso", await cliente.GetStringAsync($"/admin/videos/{video.Id}"));
    }

    [Fact]
    public async Task Liberar_um_dominio_alcanca_qualquer_email_dele()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", "/admin/acessos/dominio",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString(),
                ["dominio"] = "empresa.com",
                ["validade"] = "sempre",
                ["valorDaValidade"] = ""
            });

        using var convidado = _app.CreateBrowser();
        await EntrarAsync(convidado, "qualquer.um@empresa.com");

        Assert.Equal(HttpStatusCode.OK, (await convidado.GetAsync($"/v/{video.Slug}")).StatusCode);
    }

    [Fact]
    public async Task O_link_criado_aparece_uma_unica_vez()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", "/admin/acessos/link",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString(),
                ["validade"] = "sempre",
                ["valorDaValidade"] = "",
                ["limiteDeVisualizacoes"] = ""
            });

        var destino = resposta.Headers.Location!.ToString();

        // O segredo não vai na URL: ela carrega apenas a chave de recuperação.
        Assert.DoesNotContain("/l/", destino);

        var primeira = await cliente.GetStringAsync(destino);
        Assert.Contains("/l/", primeira);
        Assert.Contains("não será exibido de novo", primeira);

        var segunda = await cliente.GetStringAsync(destino);
        Assert.DoesNotContain("não será exibido de novo", segunda);
    }

    [Fact]
    public async Task O_link_criado_pela_interface_dá_acesso()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", "/admin/acessos/link",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString(),
                ["validade"] = "sempre",
                ["valorDaValidade"] = "",
                ["limiteDeVisualizacoes"] = ""
            });

        var pagina = await cliente.GetStringAsync(resposta.Headers.Location!.ToString());
        var endereco = pagina.Split("value=\"").First(p => p.Contains("/l/")).Split('"')[0];
        var caminho = new Uri(endereco).PathAndQuery;

        using var visitante = _app.CreateBrowser();
        await visitante.GetAsync(caminho);

        Assert.Equal(HttpStatusCode.OK, (await visitante.GetAsync($"/v/{video.Slug}")).StatusCode);
    }

    [Fact]
    public async Task Revogar_pela_interface_corta_o_acesso()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;
        _app.Emails.Clear();

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", "/admin/acessos/convidar",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString(),
                ["emails"] = Convidado,
                ["validade"] = "sempre",
                ["valorDaValidade"] = ""
            });

        using var convidado = _app.CreateBrowser();
        await convidado.GetAsync($"/entrar/{_app.Emails.LastToken()}");
        Assert.Equal(HttpStatusCode.OK, (await convidado.GetAsync($"/v/{video.Slug}")).StatusCode);

        Guid concessaoId;
        await using (var db = postgres.CreateContext())
            concessaoId = (await db.AccessGrants.SingleAsync()).Id;

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", $"/admin/acessos/{concessaoId}/revogar",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString()
            });

        Assert.Equal(HttpStatusCode.NotFound, (await convidado.GetAsync($"/v/{video.Slug}")).StatusCode);
        Assert.Contains("revogada", await cliente.GetStringAsync($"/admin/videos/{video.Id}"));
    }

    [Fact]
    public async Task Restaurar_devolve_o_acesso_revogado()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", "/admin/acessos/convidar",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString(),
                ["emails"] = Convidado,
                ["validade"] = "sempre",
                ["valorDaValidade"] = ""
            });

        Guid concessaoId;
        await using (var db = postgres.CreateContext())
            concessaoId = (await db.AccessGrants.SingleAsync()).Id;

        var campos = new Dictionary<string, string>
        {
            ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
            ["alvoId"] = video.Id.ToString()
        };

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", $"/admin/acessos/{concessaoId}/revogar", campos);
        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", $"/admin/acessos/{concessaoId}/restaurar", campos);

        await using var leitura = postgres.CreateContext();
        Assert.False((await leitura.AccessGrants.SingleAsync()).IsRevoked);
    }

    [Fact]
    public async Task Convite_sem_endereco_valido_mostra_o_erro()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", "/admin/acessos/convidar",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString(),
                ["emails"] = "nao-e-email",
                ["validade"] = "sempre",
                ["valorDaValidade"] = ""
            });

        Assert.Contains("erro=", resposta.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Convidado_nao_consegue_conceder_acesso()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Restricted);

        await using (var db = postgres.CreateContext())
        {
            db.Users.Add(OpenTube.Domain.Entities.User.Create(
                OpenTube.Domain.ValueObjects.EmailAddress.Parse(Convidado), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Convidado);

        var resposta = await cliente.PostAsync("/admin/acessos/convidar",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString(),
                ["emails"] = "invasor@exemplo.com",
                ["validade"] = "sempre"
            }));

        // Sem o papel de administrador, o pedido é desviado para a tela de entrada.
        Assert.Contains("/entrar", resposta.Headers.Location?.ToString() ?? string.Empty);

        await using var leitura = postgres.CreateContext();
        Assert.Empty(await leitura.AccessGrants.ToListAsync());
    }
}
