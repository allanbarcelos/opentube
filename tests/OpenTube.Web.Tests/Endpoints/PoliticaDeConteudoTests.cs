// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Text.RegularExpressions;
using OpenTube.TestSupport;
using OpenTube.Web.Seguranca;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Endpoints;

/// <summary>
/// A política de conteúdo só protege se nada na página depender de script escrito no próprio
/// HTML: um onclick esquecido deixa de funcionar em silêncio no navegador.
/// </summary>
[Collection(IntegrationCollection.Name)]
public partial class PoliticaDeConteudoTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private const string Admin = "admin@barcelos.dev";

    private OpenTubeWebFactory _app = default!;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        _app = new OpenTubeWebFactory(postgres, minio, Admin);
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task EntrarComoAdminAsync(HttpClient cliente)
    {
        await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/sign-in", "/sign-in/code", new Dictionary<string, string> { ["email"] = Admin });

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/sign-in?email={Uri.EscapeDataString(Admin)}&enviado=1", "/sign-in/verify",
            new Dictionary<string, string> { ["email"] = Admin, ["codigo"] = _app.Emails.LastCode() });
    }

    [Fact]
    public async Task So_executa_scripts_do_proprio_site_e_o_do_nonce()
    {
        using var cliente = _app.CreateBrowser();

        var resposta = await cliente.GetAsync("/");
        var politica = string.Join(' ', resposta.Headers.GetValues("Content-Security-Policy"));
        var nonce = NonceRegex().Match(politica).Groups[1].Value;
        var html = await resposta.Content.ReadAsStringAsync();

        Assert.NotEmpty(nonce);
        Assert.Contains("object-src 'none'", politica);
        Assert.DoesNotContain("unsafe-inline' 'nonce", politica);
        Assert.Contains($"<script type=\"importmap\" nonce=\"{nonce}\"", html);
    }

    [Fact]
    public async Task O_nonce_muda_a_cada_pedido()
    {
        using var cliente = _app.CreateBrowser();

        var primeiro = (await cliente.GetAsync("/sign-in")).Headers.GetValues("Content-Security-Policy").Single();
        var segundo = (await cliente.GetAsync("/sign-in")).Headers.GetValues("Content-Security-Policy").Single();

        Assert.NotEqual(NonceRegex().Match(primeiro).Value, NonceRegex().Match(segundo).Value);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/sign-in")]
    [InlineData("/admin")]
    [InlineData("/admin/collections")]
    [InlineData("/admin/people")]
    [InlineData("/admin/watermark")]
    [InlineData("/admin/upload")]
    [InlineData("/admin/audit")]
    [InlineData("/admin/support")]
    public async Task Nenhuma_pagina_depende_de_script_no_proprio_html(string pagina)
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var html = await cliente.GetStringAsync(pagina);

        Assert.DoesNotMatch(ManipuladorInlineRegex(), html);
        foreach (Match script in ScriptRegex().Matches(html))
            Assert.True(script.Value.Contains(" src=") || script.Value.Contains(" nonce="), $"script inline em {pagina}: {script.Value}");
    }

    [Theory]
    [InlineData("http://localhost:9000", "http://localhost:9000")]
    [InlineData("https://videos.empresa.com/", "https://videos.empresa.com")]
    [InlineData("", null)]
    [InlineData("nao-e-endereco", null)]
    public void A_origem_do_storage_entra_sem_caminho(string endereco, string? esperado) =>
        Assert.Equal(esperado, PoliticaDeConteudo.OrigemDe(endereco));

    [GeneratedRegex(@"'nonce-([^']+)'")]
    private static partial Regex NonceRegex();

    [GeneratedRegex(@"\son(click|focus|change|submit|load|error|input|mouseover)\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex ManipuladorInlineRegex();

    [GeneratedRegex(@"<script\b[^>]*>")]
    private static partial Regex ScriptRegex();
}
