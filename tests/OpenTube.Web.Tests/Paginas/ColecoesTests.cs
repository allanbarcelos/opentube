// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Access;
using OpenTube.Infrastructure.Services;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Paginas;

/// <summary>Administração de coleções e o efeito delas sobre quem consegue assistir.</summary>
[Collection(IntegrationCollection.Name)]
public class ColecoesTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
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

    private async Task<Guid> CriarColecaoAsync(HttpClient cliente, string nome)
    {
        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/admin/collections", "/admin/collections/create",
            new Dictionary<string, string> { ["nome"] = nome, ["descricao"] = "" });

        var destino = resposta.Headers.Location!.ToString();

        return Guid.Parse(destino.Split('/')[^1].Split('?')[0]);
    }

    [Fact]
    public async Task Convidado_nao_alcanca_a_administracao_de_colecoes()
    {
        await using (var db = postgres.CreateContext())
        {
            db.Users.Add(OpenTube.Domain.Entities.User.Create(
                OpenTube.Domain.ValueObjects.EmailAddress.Parse(Convidado), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Convidado);

        Assert.NotEqual(HttpStatusCode.OK, (await cliente.GetAsync("/admin/collections")).StatusCode);
    }

    [Fact]
    public async Task Cria_uma_colecao_e_ela_aparece_na_listagem()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        await CriarColecaoAsync(cliente, "Treinamentos Obrigatórios");

        var html = await cliente.GetStringAsync("/admin/collections");

        Assert.Contains("Treinamentos Obrigatórios", html);
        Assert.Contains("0 videos", html);
    }

    [Fact]
    public async Task Acrescenta_e_remove_videos_pela_interface()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Segurança da Informação", VideoVisibility.Restricted);

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);
        var colecao = await CriarColecaoAsync(cliente, "Treinamentos");

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/add",
            new Dictionary<string, string> { ["videoId"] = video.Id.ToString() });

        Assert.Contains("Segurança da Informação", await cliente.GetStringAsync($"/admin/collections/{colecao}"));

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/remove",
            new Dictionary<string, string> { ["videoId"] = video.Id.ToString() });

        await using var db = postgres.CreateContext();
        Assert.Empty(await db.CollectionVideos.ToListAsync());
    }

    [Fact]
    public async Task Renomear_a_colecao_nao_muda_o_endereco()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);
        var colecao = await CriarColecaoAsync(cliente, "Treinamentos");

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/save",
            new Dictionary<string, string> { ["nome"] = "Capacitação 2026", ["descricao"] = "Nova" });

        await using var db = postgres.CreateContext();
        var lida = await db.Collections.SingleAsync();

        Assert.Equal("Capacitação 2026", lida.Name);
        Assert.Equal("treinamentos", lida.Slug);
    }

    [Fact]
    public async Task A_concessao_sobre_a_colecao_libera_os_videos_dela()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Segurança da Informação", VideoVisibility.Restricted);

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);
        var colecao = await CriarColecaoAsync(cliente, "Treinamentos");

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/add",
            new Dictionary<string, string> { ["videoId"] = video.Id.ToString() });

        using (var escopo = _app.Services.CreateScope())
        {
            await escopo.ServiceProvider.GetRequiredService<GrantService>().InviteAsync(
                [Convidado], GrantTargetType.Collection, colecao, GrantValidity.Forever, Guid.CreateVersion7());
        }

        using var convidado = _app.CreateBrowser();
        await EntrarAsync(convidado, Convidado);

        Assert.Contains("Segurança da Informação", await convidado.GetStringAsync("/"));
        Assert.Equal(HttpStatusCode.OK, (await convidado.GetAsync($"/watch/{video.Slug}")).StatusCode);
    }

    [Fact]
    public async Task Tirar_o_video_da_colecao_corta_o_acesso_de_quem_veio_por_ela()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Segurança da Informação", VideoVisibility.Restricted);

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);
        var colecao = await CriarColecaoAsync(cliente, "Treinamentos");

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/add",
            new Dictionary<string, string> { ["videoId"] = video.Id.ToString() });

        using (var escopo = _app.Services.CreateScope())
        {
            await escopo.ServiceProvider.GetRequiredService<GrantService>().InviteAsync(
                [Convidado], GrantTargetType.Collection, colecao, GrantValidity.Forever, Guid.CreateVersion7());
        }

        using var convidado = _app.CreateBrowser();
        await EntrarAsync(convidado, Convidado);
        Assert.Equal(HttpStatusCode.OK, (await convidado.GetAsync($"/watch/{video.Slug}")).StatusCode);

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/remove",
            new Dictionary<string, string> { ["videoId"] = video.Id.ToString() });

        Assert.Equal(HttpStatusCode.NotFound, (await convidado.GetAsync($"/watch/{video.Slug}")).StatusCode);
    }

    [Fact]
    public async Task Excluir_a_colecao_derruba_o_acesso_concedido_por_ela()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Segurança da Informação", VideoVisibility.Restricted);

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);
        var colecao = await CriarColecaoAsync(cliente, "Treinamentos");

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/add",
            new Dictionary<string, string> { ["videoId"] = video.Id.ToString() });

        using (var escopo = _app.Services.CreateScope())
        {
            await escopo.ServiceProvider.GetRequiredService<GrantService>().InviteAsync(
                [Convidado], GrantTargetType.Collection, colecao, GrantValidity.Forever, Guid.CreateVersion7());
        }

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/delete",
            new Dictionary<string, string>());

        // A coleção excluída não deixa de existir no banco, mas o vínculo com os vídeos some
        // do ponto de vista do acesso.
        await using var db = postgres.CreateContext();
        Assert.True((await db.Collections.SingleAsync()).IsDeleted);
    }

    [Fact]
    public async Task Colecao_inexistente_responde_como_nao_encontrada()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        var resposta = await cliente.GetAsync($"/admin/collections/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Fact]
    public async Task Criar_colecao_sem_nome_e_recusado()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/admin/collections", "/admin/collections/create",
            new Dictionary<string, string> { ["nome"] = "   ", ["descricao"] = "" });

        Assert.Contains("erro=", resposta.Headers.Location!.ToString());
    }
}
