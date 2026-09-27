// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Analytics;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Paginas;

/// <summary>Painéis de audiência e exportação dos relatórios.</summary>
[Collection(IntegrationCollection.Name)]
public class PaineisDeAudienciaTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
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

    private async Task<Guid> AssistirAsync(HttpClient cliente, Guid videoId, double ate, bool concluiu = false)
    {
        var abertura = await cliente.PostAsJsonAsync("/api/playback/start", new { videoId });
        abertura.EnsureSuccessStatusCode();

        var sessao = (await abertura.Content.ReadFromJsonAsync<SessaoResposta>())!.SessaoId;

        var eventos = new List<object> { new { tipo = "progress", em = ate, de = 0d, ate } };
        if (concluiu)
            eventos.Add(new { tipo = "ended", em = ate, de = (double?)null, ate = (double?)null });

        await cliente.PostAsJsonAsync($"/api/playback/{sessao}/events", new { eventos });

        return sessao;
    }

    private async Task AgregarAsync()
    {
        using var escopo = _app.Services.CreateScope();
        await escopo.ServiceProvider.GetRequiredService<AnalyticsAggregator>().RollupRecentAsync();
    }

    [Fact]
    public async Task O_painel_mostra_o_resumo_da_audiencia()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var visitante = _app.CreateBrowser();
        await AssistirAsync(visitante, video.Id, 125, concluiu: true);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var html = await cliente.GetStringAsync($"/admin/videos/{video.Id}?tab=audience");

        Assert.Contains("Views", html);
        Assert.Contains("Distinct people", html);
        Assert.Contains("Reached the end", html);
        Assert.Contains("Unidentified visitor", html);
    }

    [Fact]
    public async Task A_curva_de_retencao_aparece_depois_da_agregacao()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var visitante = _app.CreateBrowser();
        await AssistirAsync(visitante, video.Id, 125, concluiu: true);
        await AgregarAsync();

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var html = await cliente.GetStringAsync($"/admin/videos/{video.Id}?tab=audience");

        Assert.Contains("Where the audience drops off", html);
        Assert.Contains("data-grafico-retencao", html);
        Assert.Contains("grafico-linha", html);
        // A tabela ao lado do gráfico é o que torna o dado legível sem depender da cor.
        Assert.Contains("See the numbers", html);
    }

    [Fact]
    public async Task Sem_audiencia_o_painel_explica_a_ausencia_da_curva()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var html = await cliente.GetStringAsync($"/admin/videos/{video.Id}?tab=audience");

        Assert.Contains("Not enough audience yet", html);
        Assert.Contains("Nobody has watched this video yet", html);
    }

    [Fact]
    public async Task O_caminho_do_convite_aparece_com_quem_ainda_nao_assistiu()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Restricted);

        using (var escopo = _app.Services.CreateScope())
        {
            await escopo.ServiceProvider.GetRequiredService<OpenTube.Infrastructure.Access.GrantService>()
                .InviteAsync(["pendente@barcelos.dev"], GrantTargetType.Video, video.Id,
                    OpenTube.Infrastructure.Access.GrantValidity.Forever, Guid.CreateVersion7());
        }

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var html = await cliente.GetStringAsync($"/admin/videos/{video.Id}?tab=audience");

        Assert.Contains("From the invite to the end of the video", html);
        Assert.Contains("Have not watched yet", html);
        Assert.Contains("pendente@barcelos.dev", html);
    }

    [Fact]
    public async Task A_listagem_de_pessoas_mostra_quem_entrou()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var html = await cliente.GetStringAsync("/admin/people");

        Assert.Contains(Admin, html);
        Assert.Contains("Administrator", html);
    }

    [Fact]
    public async Task A_busca_de_pessoas_filtra_pelo_email()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        Assert.Contains(Admin, await cliente.GetStringAsync("/admin/people?q=barcelos"));
        Assert.Contains("No one matches", await cliente.GetStringAsync("/admin/people?q=ninguem"));
    }

    [Fact]
    public async Task A_pagina_da_pessoa_mostra_o_que_ela_assistiu()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        await AssistirAsync(cliente, video.Id, 125, concluiu: true);

        Guid usuarioId;
        await using (var db = postgres.CreateContext())
            usuarioId = (await db.Users.SingleAsync(u => u.Email == Admin)).Id;

        var html = await cliente.GetStringAsync($"/admin/people/{usuarioId}");

        Assert.Contains("Boas-vindas", html);
        Assert.Contains("to the end", html);
    }

    [Fact]
    public async Task Pessoa_inexistente_responde_como_nao_encontrada()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var resposta = await cliente.GetAsync($"/admin/people/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Fact]
    public async Task Exporta_os_espectadores_de_um_video()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Reunião Trimestral", VideoVisibility.Public);

        using var visitante = _app.CreateBrowser();
        await AssistirAsync(visitante, video.Id, 125, concluiu: true);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var resposta = await cliente.GetAsync($"/admin/export/videos/{video.Id}/viewers.csv");
        var csv = await resposta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("text/csv", resposta.Content.Headers.ContentType!.MediaType);
        Assert.Contains("reuniao-trimestral", resposta.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Contains("Person;Sessions", csv);
        Assert.Contains("yes", csv);
    }

    [Fact]
    public async Task Exporta_a_atividade_de_uma_pessoa()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        await AssistirAsync(cliente, video.Id, 125);

        Guid usuarioId;
        await using (var db = postgres.CreateContext())
            usuarioId = (await db.Users.SingleAsync(u => u.Email == Admin)).Id;

        var resposta = await cliente.GetAsync($"/admin/export/people/{usuarioId}/activity.csv");
        var csv = await resposta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Contains("Boas-vindas", csv);
    }

    [Fact]
    public async Task A_exportacao_exige_ser_administrador()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var resposta = await cliente.GetAsync($"/admin/export/videos/{video.Id}/viewers.csv");

        Assert.Equal(HttpStatusCode.Found, resposta.StatusCode);
        Assert.Contains("/sign-in", resposta.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Exportar_video_inexistente_responde_como_nao_encontrado()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var resposta = await cliente.GetAsync($"/admin/export/videos/{Guid.CreateVersion7()}/viewers.csv");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    private sealed record SessaoResposta(Guid SessaoId);
}
