// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using Microsoft.EntityFrameworkCore;
using OpenTube.Infrastructure.Persistence;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Paginas;

/// <summary>Percurso de entrada sem senha, exercitado pela interface de verdade.</summary>
[Collection(IntegrationCollection.Name)]
public class AcessoTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private const string Admin = "allan@barcelos.dev";

    private OpenTubeWebFactory _app = default!;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        _app = new OpenTubeWebFactory(postgres, minio, Admin);

        // Força a inicialização da aplicação, que promove os administradores configurados.
        using var cliente = _app.CreateBrowser();
        await cliente.GetAsync("/health");
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task A_tela_de_entrada_nao_pede_senha()
    {
        using var cliente = _app.CreateBrowser();

        var html = await cliente.GetStringAsync("/sign-in");

        Assert.Contains("six-digit code", html);
        Assert.DoesNotContain("type=\"password\"", html);
    }

    [Fact]
    public async Task O_administrador_configurado_e_criado_ao_subir_a_aplicacao()
    {
        await using var db = postgres.CreateContext();

        var usuario = await db.Users.SingleAsync(u => u.Email == Admin);

        Assert.True(usuario.IsAdmin);
    }

    [Fact]
    public async Task Pedir_codigo_envia_email_e_leva_a_tela_do_codigo()
    {
        using var cliente = _app.CreateBrowser();

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/sign-in", "/sign-in/code", new Dictionary<string, string> { ["email"] = Admin });

        Assert.Equal(HttpStatusCode.Found, resposta.StatusCode);
        Assert.Contains("enviado=1", resposta.Headers.Location!.ToString());
        Assert.Single(_app.Emails.Sent);
    }

    [Fact]
    public async Task Endereco_desconhecido_recebe_a_mesma_resposta_e_nenhum_email()
    {
        using var cliente = _app.CreateBrowser();

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/sign-in", "/sign-in/code", new Dictionary<string, string> { ["email"] = "ninguem@exemplo.com" });

        Assert.Equal(HttpStatusCode.Found, resposta.StatusCode);
        Assert.Contains("enviado=1", resposta.Headers.Location!.ToString());
        Assert.Empty(_app.Emails.Sent);
    }

    [Fact]
    public async Task Endereco_invalido_volta_com_mensagem()
    {
        using var cliente = _app.CreateBrowser();

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/sign-in", "/sign-in/code", new Dictionary<string, string> { ["email"] = "nao-e-email" });

        Assert.Contains("erro=", resposta.Headers.Location!.ToString());
        Assert.Empty(_app.Emails.Sent);
    }

    [Fact]
    public async Task Entra_com_o_codigo_recebido_e_e_reconhecido_como_administrador()
    {
        using var cliente = _app.CreateBrowser();
        await PedirCodigoAsync(cliente, Admin);

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente,
            $"/sign-in?email={Uri.EscapeDataString(Admin)}&enviado=1",
            "/sign-in/verify",
            new Dictionary<string, string> { ["email"] = Admin, ["codigo"] = _app.Emails.LastCode() });

        Assert.Equal(HttpStatusCode.Found, resposta.StatusCode);
        Assert.Equal("/admin", resposta.Headers.Location!.ToString());

        var home = await cliente.GetStringAsync("/");
        Assert.Contains("Administration", home);
        Assert.Contains(Admin, home);
    }

    [Fact]
    public async Task Codigo_errado_volta_com_mensagem_e_sem_sessao()
    {
        using var cliente = _app.CreateBrowser();
        await PedirCodigoAsync(cliente, Admin);

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente,
            $"/sign-in?email={Uri.EscapeDataString(Admin)}&enviado=1",
            "/sign-in/verify",
            new Dictionary<string, string> { ["email"] = Admin, ["codigo"] = "000000" });

        Assert.Contains("erro=", resposta.Headers.Location!.ToString());
        Assert.DoesNotContain("Administration", await cliente.GetStringAsync("/"));
    }

    [Fact]
    public async Task O_link_do_email_entra_sem_digitar_o_codigo()
    {
        using var cliente = _app.CreateBrowser();
        await PedirCodigoAsync(cliente, Admin);

        var resposta = await cliente.GetAsync($"/sign-in/{_app.Emails.LastToken()}");

        Assert.Equal(HttpStatusCode.Found, resposta.StatusCode);
        Assert.Equal("/admin", resposta.Headers.Location!.ToString());
        Assert.Contains("Administration", await cliente.GetStringAsync("/"));
    }

    [Fact]
    public async Task O_link_usado_uma_vez_nao_serve_de_novo()
    {
        using var cliente = _app.CreateBrowser();
        await PedirCodigoAsync(cliente, Admin);
        var token = _app.Emails.LastToken();

        await cliente.GetAsync($"/sign-in/{token}");

        using var outro = _app.CreateBrowser();
        var resposta = await outro.GetAsync($"/sign-in/{token}");

        Assert.Contains("erro=", resposta.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Sair_encerra_a_sessao()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        var resposta = await FormularioHelpers.EnviarFormularioAsync(cliente, "/", "/sign-out", new Dictionary<string, string>());

        Assert.Equal(HttpStatusCode.Found, resposta.StatusCode);

        var home = await cliente.GetStringAsync("/");
        Assert.DoesNotContain("Administration", home);
        Assert.Contains("Sign in", home);
    }

    [Fact]
    public async Task Sessao_encerrada_no_banco_deixa_de_valer_na_proxima_requisicao()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        await using (var db = postgres.CreateContext())
        {
            var sessao = await db.AuthSessions.SingleAsync();
            sessao.Revoke(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        // O cookie continua no navegador, mas a sessão não existe mais: o acesso cai na hora.
        Assert.DoesNotContain("Administration", await cliente.GetStringAsync("/"));
    }

    [Fact]
    public async Task Formulario_sem_campo_antifalsificacao_e_recusado()
    {
        using var cliente = _app.CreateBrowser();

        var resposta = await cliente.PostAsync("/sign-in/code",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = Admin }));

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Empty(_app.Emails.Sent);
    }

    private async Task PedirCodigoAsync(HttpClient cliente, string email)
    {
        _app.Emails.Clear();

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/sign-in", "/sign-in/code", new Dictionary<string, string> { ["email"] = email });
    }

    internal async Task EntrarAsync(HttpClient cliente, string email)
    {
        await PedirCodigoAsync(cliente, email);

        await FormularioHelpers.EnviarFormularioAsync(
            cliente,
            $"/sign-in?email={Uri.EscapeDataString(email)}&enviado=1",
            "/sign-in/verify",
            new Dictionary<string, string> { ["email"] = email, ["codigo"] = _app.Emails.LastCode() });
    }
}
