// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using System.Text.Json;
using OpenTube.Domain.Enums;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Paginas;

/// <summary>
/// Seletores da administração que vêm do banco: a página não traz a lista, e as opções chegam
/// por busca, uma página por vez, só para quem administra.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class SeletorDinamicoTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
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

    private static async Task<Guid> CriarColecaoAsync(HttpClient cliente, string nome)
    {
        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/admin/collections", "/admin/collections/create",
            new Dictionary<string, string> { ["nome"] = nome, ["descricao"] = "" });

        return Guid.Parse(resposta.Headers.Location!.ToString().Split('/')[^1].Split('?')[0]);
    }

    [Fact]
    public async Task A_pagina_da_colecao_nao_traz_a_lista_de_videos_e_aponta_para_a_busca()
    {
        using var storage = minio.CreateStorage();
        var dentro = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);
        await AcervoDeTeste.PublicarAsync(postgres, storage, "Vídeo que não deve vir na página", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        var colecao = await CriarColecaoAsync(cliente, "Integração");
        await CriarColecaoAsync(cliente, "Outra coleção que não deve vir na página");

        // Com um vídeo dentro, a exclusão da coleção oferece mover os vídeos: o segundo seletor.
        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/add",
            new Dictionary<string, string> { ["videoId"] = dentro.Id.ToString() });

        var html = await cliente.GetStringAsync($"/admin/collections/{colecao}");

        Assert.Contains($"data-fonte=\"/admin/lookup/collections/{colecao}/videos\"", html);
        Assert.Contains($"data-fonte=\"/admin/lookup/collections?except={colecao}\"", html);
        Assert.DoesNotContain("<select class=\"form-select\" id=\"videoId\"", html);
        Assert.DoesNotContain("Vídeo que não deve vir na página", html);
        Assert.DoesNotContain("Outra coleção que não deve vir na página", html);
    }

    [Fact]
    public async Task A_busca_devolve_uma_pagina_em_json_filtrada_pelo_termo()
    {
        using var storage = minio.CreateStorage();
        await AcervoDeTeste.PublicarAsync(postgres, storage, "Reunião Trimestral", VideoVisibility.Public);
        await AcervoDeTeste.PublicarAsync(postgres, storage, "Planejamento", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        var colecao = await CriarColecaoAsync(cliente, "Diretoria");

        var resposta = await cliente.GetAsync($"/admin/lookup/collections/{colecao}/videos?q=reuniao&page=1");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.True(resposta.Headers.CacheControl!.NoStore);

        using var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        var itens = json.RootElement.GetProperty("items");

        Assert.Equal(1, itens.GetArrayLength());
        Assert.Equal("Reunião Trimestral", itens[0].GetProperty("label").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("page").GetInt32());
        Assert.False(json.RootElement.GetProperty("hasMore").GetBoolean());
    }

    [Theory]
    [InlineData("/admin/lookup/collections")]
    [InlineData("/admin/lookup/collections/00000000-0000-0000-0000-000000000001/videos")]
    public async Task Quem_nao_administra_nao_recebe_as_opcoes(string endereco)
    {
        using var visitante = _app.CreateBrowser();

        var resposta = await visitante.GetAsync(endereco);

        Assert.NotEqual(HttpStatusCode.OK, resposta.StatusCode);
        Assert.NotEqual("application/json", resposta.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Adicionar_sem_escolher_um_video_volta_dizendo_o_que_faltou()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        var colecao = await CriarColecaoAsync(cliente, "Integração");

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/add",
            new Dictionary<string, string> { ["videoId"] = "" });

        Assert.Equal(HttpStatusCode.Redirect, resposta.StatusCode);
        Assert.Contains("erro=", resposta.Headers.Location!.ToString());
    }
}
