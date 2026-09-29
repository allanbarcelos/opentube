// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using System.Text.RegularExpressions;
using OpenTube.Domain.Enums;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Paginas;

/// <summary>
/// Paginação da home e do painel: o link da página seguinte leva a mesma busca, e as páginas
/// juntas trazem cada vídeo uma vez só — inclusive quando todos têm a mesma data.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PaginacaoTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private const string Admin = "allan@barcelos.dev";

    private OpenTubeWebFactory _app = default!;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        _app = new OpenTubeWebFactory(postgres, minio, Admin);

        // O ajudante grava todos com o mesmo instante: é o pior caso para a ordem entre páginas.
        using var storage = minio.CreateStorage();
        for (var i = 1; i <= 30; i++)
            await AcervoDeTeste.PublicarAsync(postgres, storage, $"Aula {i:00}", VideoVisibility.Public);
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task As_paginas_da_home_trazem_cada_video_uma_vez()
    {
        using var cliente = _app.CreateBrowser();

        var primeira = await cliente.GetStringAsync("/");
        var seguinte = LinkDaProxima(primeira);
        Assert.Equal("/?page=2", seguinte);

        var segunda = await cliente.GetStringAsync(seguinte);

        var todos = Videos(primeira).Concat(Videos(segunda)).ToList();
        Assert.Equal(30, todos.Count);
        Assert.Equal(30, todos.Distinct().Count());
        Assert.Contains("Page 2 of 2", segunda);
    }

    [Fact]
    public async Task A_pagina_seguinte_da_busca_continua_na_mesma_busca()
    {
        using var cliente = _app.CreateBrowser();

        var primeira = await cliente.GetStringAsync("/?q=Aula");
        var seguinte = LinkDaProxima(primeira);
        Assert.Equal("/?page=2&q=Aula", seguinte);

        var segunda = await cliente.GetStringAsync(seguinte);
        Assert.Equal(30, Videos(primeira).Concat(Videos(segunda)).Distinct().Count());
        Assert.Contains("value=\"Aula\"", segunda);
    }

    [Fact]
    public async Task O_painel_pagina_com_a_mesma_busca()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        Assert.Equal("/admin?page=2", LinkDaProxima(await cliente.GetStringAsync("/admin")));
        Assert.Equal("/admin?page=2&q=Aula", LinkDaProxima(await cliente.GetStringAsync("/admin?q=Aula")));
    }

    private static string LinkDaProxima(string html) =>
        WebUtility.HtmlDecode(Regex.Match(html, "<a class=\"page-link\" href=\"([^\"]*)\">Next").Groups[1].Value);

    private static IEnumerable<string> Videos(string html) =>
        Regex.Matches(html, "href=\"/watch/([^\"]+)\"").Select(m => m.Groups[1].Value).Distinct();

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
}
