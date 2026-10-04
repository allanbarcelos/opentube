// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Access;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Endpoints;

/// <summary>
/// Entrada por link secreto: quem tem o endereço assiste sem se identificar, e a revogação
/// corta o acesso na requisição seguinte.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class LinkDeCompartilhamentoTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private const string Admin = "allan@barcelos.dev";

    private OpenTubeWebFactory _app = default!;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        _app = new OpenTubeWebFactory(postgres, minio, Admin);

        using var cliente = _app.CreateBrowser();
        await cliente.GetAsync("/health");
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task<(string Url, Guid GrantId, string Slug)> CriarLinkAsync(int? maxViews = null)
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Restricted);

        using var escopo = _app.Services.CreateScope();
        var links = escopo.ServiceProvider.GetRequiredService<ShareLinkService>();

        var link = await links.CreateShareLinkAsync(
            GrantTargetType.Video, video.Id, GrantValidity.Forever, Guid.CreateVersion7(), maxViews);

        return (link.Url, link.GrantId, video.Slug);
    }

    private static string Caminho(string url) => new Uri(url).PathAndQuery;

    [Fact]
    public async Task O_link_leva_direto_ao_video()
    {
        var (url, _, slug) = await CriarLinkAsync();

        using var cliente = _app.CreateBrowser();
        var resposta = await cliente.GetAsync(Caminho(url));

        Assert.Equal(HttpStatusCode.Found, resposta.StatusCode);
        Assert.Equal($"/watch/{slug}", resposta.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Depois_do_link_o_visitante_assiste_sem_se_identificar()
    {
        var (url, _, slug) = await CriarLinkAsync();

        using var cliente = _app.CreateBrowser();
        await cliente.GetAsync(Caminho(url));

        var pagina = await cliente.GetAsync($"/watch/{slug}");

        Assert.Equal(HttpStatusCode.OK, pagina.StatusCode);
        Assert.Contains("Plano Confidencial", await pagina.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task O_video_restrito_tambem_passa_a_aparecer_na_home()
    {
        var (url, _, _) = await CriarLinkAsync();

        using var cliente = _app.CreateBrowser();

        Assert.DoesNotContain("Plano Confidencial", await cliente.GetStringAsync("/"));

        await cliente.GetAsync(Caminho(url));

        Assert.Contains("Plano Confidencial", await cliente.GetStringAsync("/"));
    }

    [Fact]
    public async Task A_reproducao_e_liberada_pelo_link()
    {
        var (url, _, slug) = await CriarLinkAsync();

        using var cliente = _app.CreateBrowser();
        await cliente.GetAsync(Caminho(url));

        var manifesto = await Reproducao.ManifestoDaPaginaAsync(cliente, slug);

        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync(manifesto)).StatusCode);
    }

    [Fact]
    public async Task Quem_nao_tem_o_link_continua_sem_acesso()
    {
        var (_, _, slug) = await CriarLinkAsync();

        using var cliente = _app.CreateBrowser();

        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync($"/watch/{slug}")).StatusCode);
    }

    [Fact]
    public async Task Token_inventado_nao_abre_nada()
    {
        await CriarLinkAsync();

        using var cliente = _app.CreateBrowser();
        var resposta = await cliente.GetAsync("/link/token-inventado");

        Assert.Equal(HttpStatusCode.Found, resposta.StatusCode);
        Assert.Equal("/not-found", resposta.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Revogar_corta_o_acesso_de_quem_ja_tinha_o_link()
    {
        var (url, grantId, slug) = await CriarLinkAsync();

        using var cliente = _app.CreateBrowser();
        await cliente.GetAsync(Caminho(url));
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync($"/watch/{slug}")).StatusCode);

        using (var escopo = _app.Services.CreateScope())
            await escopo.ServiceProvider.GetRequiredService<GrantService>().RevokeAsync(grantId);

        // O cookie continua no navegador, mas a concessão é conferida a cada requisição.
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync($"/watch/{slug}")).StatusCode);
    }

    [Fact]
    public async Task Link_revogado_nao_abre_mais_nem_na_entrada()
    {
        var (url, grantId, _) = await CriarLinkAsync();

        using (var escopo = _app.Services.CreateScope())
            await escopo.ServiceProvider.GetRequiredService<GrantService>().RevokeAsync(grantId);

        using var cliente = _app.CreateBrowser();
        var resposta = await cliente.GetAsync(Caminho(url));

        Assert.Equal("/not-found", resposta.Headers.Location!.ToString());
    }

    [Fact]
    public async Task O_limite_de_visualizacoes_encerra_o_link()
    {
        var (url, _, slug) = await CriarLinkAsync(maxViews: 1);

        using var cliente = _app.CreateBrowser();
        await cliente.GetAsync(Caminho(url));

        var manifesto = await Reproducao.ManifestoDaPaginaAsync(cliente, slug);

        // A primeira playlist principal conta como uma visualização.
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync(manifesto)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync(manifesto)).StatusCode);
    }

    [Fact]
    public async Task A_reproducao_que_usa_a_ultima_visualizacao_toca_ate_o_fim()
    {
        var (url, _, slug) = await CriarLinkAsync(maxViews: 1);

        using var cliente = _app.CreateBrowser();
        await cliente.GetAsync(Caminho(url));

        var html = await cliente.GetStringAsync($"/watch/{slug}");
        var videoId = html.Split("/api/videos/")[1].Split('/')[0];
        var manifesto = await Reproducao.ManifestoDaPaginaAsync(cliente, slug);

        var principal = await cliente.GetAsync(manifesto);
        Assert.Equal(HttpStatusCode.OK, principal.StatusCode);

        // A versão, a coleta de audiência e a página seguinte fazem parte da mesma reprodução
        // ou de uma nova: só as primeiras podem passar.
        var versao = Reproducao.PrimeiraVersao(await principal.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync(versao)).StatusCode);

        var sessao = await cliente.PostAsJsonAsync("/api/playback/start", new { videoId });
        Assert.Equal(HttpStatusCode.OK, sessao.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync($"/watch/{slug}")).StatusCode);
    }
}
