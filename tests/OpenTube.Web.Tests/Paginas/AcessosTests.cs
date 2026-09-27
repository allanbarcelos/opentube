// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

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
        await cliente.GetAsync("/health");
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task EntrarAsync(HttpClient cliente, string email)
    {
        _app.Emails.Clear();

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/sign-in", "/sign-in/code", new Dictionary<string, string> { ["email"] = email });

        await FormularioHelpers.EnviarFormularioAsync(
            cliente,
            $"/sign-in?email={Uri.EscapeDataString(email)}&enviado=1",
            "/sign-in/verify",
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
            cliente, $"/admin/videos/{video.Id}", "/admin/access/invite",
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
        await convidado.GetAsync($"/sign-in/{_app.Emails.LastToken()}");

        Assert.Equal(HttpStatusCode.OK, (await convidado.GetAsync($"/watch/{video.Slug}")).StatusCode);
    }

    [Fact]
    public async Task A_pagina_do_video_lista_quem_tem_acesso()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", "/admin/access/invite",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString(),
                ["emails"] = Convidado,
                ["validade"] = "sempre",
                ["valorDaValidade"] = "",
                ["nota"] = "auditoria externa"
            });

        var html = await cliente.GetStringAsync($"/admin/videos/{video.Id}?tab=access");

        Assert.Contains(Convidado, html);
        Assert.Contains("auditoria externa", html);
        Assert.Contains("no end date", html);
    }

    [Fact]
    public async Task Convite_com_prazo_relativo_aparece_descrito()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", "/admin/access/invite",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString(),
                ["emails"] = Convidado,
                ["validade"] = "dias",
                ["valorDaValidade"] = "30"
            });

        Assert.Contains("30 days from the first visit", await cliente.GetStringAsync($"/admin/videos/{video.Id}?tab=access"));
    }

    [Fact]
    public async Task Liberar_um_dominio_alcanca_qualquer_email_dele()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", "/admin/access/domain",
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

        Assert.Equal(HttpStatusCode.OK, (await convidado.GetAsync($"/watch/{video.Slug}")).StatusCode);
    }

    [Fact]
    public async Task O_link_criado_aparece_uma_unica_vez()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", "/admin/access/link",
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
        Assert.DoesNotContain("/link/", destino);

        var primeira = await cliente.GetStringAsync(destino);
        Assert.Contains("/link/", primeira);
        Assert.Contains("will not be shown again.", primeira);

        var segunda = await cliente.GetStringAsync(destino);
        Assert.DoesNotContain("will not be shown again.", segunda);
    }

    [Fact]
    public async Task O_link_criado_pela_interface_dá_acesso()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", "/admin/access/link",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString(),
                ["validade"] = "sempre",
                ["valorDaValidade"] = "",
                ["limiteDeVisualizacoes"] = ""
            });

        var pagina = await cliente.GetStringAsync(resposta.Headers.Location!.ToString());
        var endereco = pagina.Split("value=\"").First(p => p.Contains("/link/")).Split('"')[0];
        var caminho = new Uri(endereco).PathAndQuery;

        using var visitante = _app.CreateBrowser();
        await visitante.GetAsync(caminho);

        Assert.Equal(HttpStatusCode.OK, (await visitante.GetAsync($"/watch/{video.Slug}")).StatusCode);
    }

    [Fact]
    public async Task Revogar_pela_interface_corta_o_acesso()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;
        _app.Emails.Clear();

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", "/admin/access/invite",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString(),
                ["emails"] = Convidado,
                ["validade"] = "sempre",
                ["valorDaValidade"] = ""
            });

        using var convidado = _app.CreateBrowser();
        await convidado.GetAsync($"/sign-in/{_app.Emails.LastToken()}");
        Assert.Equal(HttpStatusCode.OK, (await convidado.GetAsync($"/watch/{video.Slug}")).StatusCode);

        Guid concessaoId;
        await using (var db = postgres.CreateContext())
            concessaoId = (await db.AccessGrants.SingleAsync()).Id;

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", $"/admin/access/{concessaoId}/revoke",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString()
            });

        Assert.Equal(HttpStatusCode.NotFound, (await convidado.GetAsync($"/watch/{video.Slug}")).StatusCode);
        Assert.Contains("Revoked", await cliente.GetStringAsync($"/admin/videos/{video.Id}?tab=access"));
    }

    [Fact]
    public async Task Restaurar_devolve_o_acesso_revogado()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", "/admin/access/invite",
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
            cliente, $"/admin/videos/{video.Id}", $"/admin/access/{concessaoId}/revoke", campos);
        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", $"/admin/access/{concessaoId}/restore", campos);

        await using var leitura = postgres.CreateContext();
        Assert.False((await leitura.AccessGrants.SingleAsync()).IsRevoked);
    }

    [Fact]
    public async Task Convite_sem_endereco_valido_mostra_o_erro()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", "/admin/access/invite",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString(),
                ["emails"] = "nao-e-email",
                ["validade"] = "sempre",
                ["valorDaValidade"] = ""
            });

        var destino = resposta.Headers.Location!.ToString();
        Assert.Contains("tab=access", destino);
        Assert.Contains("erro-acesso=", destino);
        Assert.Contains("No valid email address was given.", System.Net.WebUtility.HtmlDecode(await cliente.GetStringAsync(destino)));
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

        var resposta = await cliente.PostAsync("/admin/access/invite",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString(),
                ["emails"] = "invasor@exemplo.com",
                ["validade"] = "sempre"
            }));

        // Sem o papel de administrador, o pedido é desviado para a tela de entrada.
        Assert.Contains("/sign-in", resposta.Headers.Location?.ToString() ?? string.Empty);

        await using var leitura = postgres.CreateContext();
        Assert.Empty(await leitura.AccessGrants.ToListAsync());
    }

    private Task<HttpResponseMessage> ConvidarAsync(HttpClient cliente, Guid videoId, Dictionary<string, string> campos)
    {
        campos["alvoTipo"] = ((int)GrantTargetType.Video).ToString();
        campos["alvoId"] = videoId.ToString();
        return FormularioHelpers.EnviarFormularioAsync(cliente, $"/admin/videos/{videoId}?tab=access", "/admin/access/invite", campos);
    }

    [Fact]
    public async Task Os_convites_ficam_numa_aba_propria()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;

        var configuracao = await cliente.GetStringAsync($"/admin/videos/{video.Id}");
        Assert.Contains($"href=\"/admin/videos/{video.Id}?tab=access\"", configuracao);
        Assert.DoesNotContain("action=\"/admin/access/invite\"", configuracao);

        var aba = await cliente.GetStringAsync($"/admin/videos/{video.Id}?tab=access");
        Assert.Contains("data-convites", aba);
        Assert.Contains("data-novo-convite=\"pessoas\"", aba);
        Assert.Contains("data-novo-convite=\"dominios\"", aba);
        Assert.Contains("data-novo-convite=\"link\"", aba);
        Assert.Contains("Nobody except administrators can watch.", aba);
    }

    [Fact]
    public async Task Convidar_de_novo_cria_outro_convite_sem_mexer_no_primeiro()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;

        await ConvidarAsync(cliente, video.Id, new() { ["emails"] = Convidado, ["validade"] = "dias", ["dias"] = "30" });
        await ConvidarAsync(cliente, video.Id, new() { ["emails"] = $"{Convidado}, outra@barcelos.dev", ["validade"] = "sempre" });

        var aba = await cliente.GetStringAsync($"/admin/videos/{video.Id}?tab=access");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(aba, "data-convite=\"").Count);
        Assert.Contains("30 days from the first visit", aba);
        Assert.Contains("2 people", aba);

        await using var db = postgres.CreateContext();
        Assert.Equal(2, await db.Invitations.CountAsync());
        Assert.Equal(TimeSpan.FromDays(30),
            (await db.AccessGrants.OrderBy(g => g.CreatedAt).FirstAsync(g => g.SubjectValue == Convidado)).DurationAfterFirstUse);
    }

    [Fact]
    public async Task Varios_dominios_num_convite_liberam_cada_um()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}?tab=access", "/admin/access/domain",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString(),
                ["dominios"] = "barcelos.dev\nparceira.com.br",
                ["validade"] = "ate",
                ["dataFinal"] = "2099-12-31"
            });
        Assert.True(resposta.Headers.Location is not null, $"{(int)resposta.StatusCode}: {await resposta.Content.ReadAsStringAsync()}");
        Assert.Contains("dominios=2", resposta.Headers.Location!.ToString());

        var aba = await cliente.GetStringAsync($"/admin/videos/{video.Id}?tab=access");
        Assert.Contains("2 domains", aba);
        Assert.Contains("@barcelos.dev", aba);
        Assert.Contains("@parceira.com.br", aba);

        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.Invitations.CountAsync());
        Assert.Equal(2, await db.AccessGrants.CountAsync(g => g.SubjectType == GrantSubjectType.Domain));
    }

    [Fact]
    public async Task Revogar_o_convite_corta_todos_e_restaurar_devolve()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;
        _app.Emails.Clear();

        await ConvidarAsync(cliente, video.Id, new() { ["emails"] = Convidado, ["validade"] = "sempre" });

        using var convidado = _app.CreateBrowser();
        await convidado.GetAsync($"/sign-in/{_app.Emails.LastToken()}");
        Assert.Equal(HttpStatusCode.OK, (await convidado.GetAsync($"/watch/{video.Slug}")).StatusCode);

        Guid conviteId;
        await using (var db = postgres.CreateContext())
            conviteId = (await db.Invitations.SingleAsync()).Id;

        var campos = new Dictionary<string, string>
        {
            ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
            ["alvoId"] = video.Id.ToString()
        };

        var revogar = await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}?tab=access", $"/admin/access/invitations/{conviteId}/revoke", campos);
        Assert.Contains("convite-revogado=1", revogar.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await convidado.GetAsync($"/watch/{video.Slug}")).StatusCode);

        var aba = await cliente.GetStringAsync($"/admin/videos/{video.Id}?tab=access");
        Assert.Contains($"data-convite=\"{conviteId}\" data-situacao=\"revogado\"", aba);
        Assert.Contains($"action=\"/admin/access/invitations/{conviteId}/restore\"", aba);

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}?tab=access", $"/admin/access/invitations/{conviteId}/restore", campos);
        Assert.Equal(HttpStatusCode.OK, (await convidado.GetAsync($"/watch/{video.Slug}")).StatusCode);
    }

    [Fact]
    public async Task Sem_marcar_o_envio_o_convite_nao_manda_email()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;
        _app.Emails.Clear();

        // Desmarcado, o navegador manda só o marcador da escolha.
        var resposta = await ConvidarAsync(cliente, video.Id, new()
        {
            ["emails"] = Convidado,
            ["validade"] = "sempre",
            ["escolhaDoEmail"] = "1"
        });

        Assert.Contains("convidados=1", resposta.Headers.Location!.ToString());
        Assert.Empty(_app.Emails.Sent);
    }

    [Fact]
    public async Task Prazo_invalido_volta_com_o_motivo_e_nao_cria_convite()
    {
        var (cliente, video) = await PrepararAsync();
        using var _ = cliente;

        var resposta = await ConvidarAsync(cliente, video.Id, new() { ["emails"] = Convidado, ["validade"] = "dias", ["dias"] = "" });

        Assert.Contains("erro-acesso=", resposta.Headers.Location!.ToString());

        await using var db = postgres.CreateContext();
        Assert.Equal(0, await db.AccessGrants.CountAsync());
    }

    [Fact]
    public async Task A_colecao_usa_o_mesmo_painel_de_convites()
    {
        var (cliente, _) = await PrepararAsync();
        using var __ = cliente;

        await FormularioHelpers.EnviarFormularioAsync(cliente, "/admin/collections", "/admin/collections/create",
            new Dictionary<string, string> { ["nome"] = "Diretoria" });

        Guid colecaoId;
        await using (var db = postgres.CreateContext())
            colecaoId = (await db.Collections.SingleAsync()).Id;

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecaoId}", "/admin/access/invite",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Collection).ToString(),
                ["alvoId"] = colecaoId.ToString(),
                ["emails"] = Convidado,
                ["validade"] = "sempre"
            });
        Assert.StartsWith($"/admin/collections/{colecaoId}?convidados=1", resposta.Headers.Location!.ToString());

        var pagina = await cliente.GetStringAsync($"/admin/collections/{colecaoId}");
        Assert.Contains("data-convites", pagina);
        Assert.Contains(Convidado, pagina);
    }
}
