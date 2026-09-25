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

    private async Task<Guid> AssistirAsync(HttpClient cliente, Guid videoId, double ate, bool concluiu = false)
    {
        var abertura = await cliente.PostAsJsonAsync("/api/reproducao/iniciar", new { videoId });
        abertura.EnsureSuccessStatusCode();

        var sessao = (await abertura.Content.ReadFromJsonAsync<SessaoResposta>())!.SessaoId;

        var eventos = new List<object> { new { tipo = "progress", em = ate, de = 0d, ate } };
        if (concluiu)
            eventos.Add(new { tipo = "ended", em = ate, de = (double?)null, ate = (double?)null });

        await cliente.PostAsJsonAsync($"/api/reproducao/{sessao}/eventos", new { eventos });

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

        var html = await cliente.GetStringAsync($"/admin/videos/{video.Id}?aba=audiencia");

        Assert.Contains("Visualizações", html);
        Assert.Contains("Pessoas distintas", html);
        Assert.Contains("Chegaram ao fim", html);
        Assert.Contains("Visitante não identificado", html);
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

        var html = await cliente.GetStringAsync($"/admin/videos/{video.Id}?aba=audiencia");

        Assert.Contains("Onde o público abandona", html);
        Assert.Contains("data-grafico-retencao", html);
        Assert.Contains("grafico-linha", html);
        // A tabela ao lado do gráfico é o que torna o dado legível sem depender da cor.
        Assert.Contains("Ver os números", html);
    }

    [Fact]
    public async Task Sem_audiencia_o_painel_explica_a_ausencia_da_curva()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var html = await cliente.GetStringAsync($"/admin/videos/{video.Id}?aba=audiencia");

        Assert.Contains("Ainda não há audiência suficiente", html);
        Assert.Contains("Ninguém assistiu a este vídeo ainda", html);
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

        var html = await cliente.GetStringAsync($"/admin/videos/{video.Id}?aba=audiencia");

        Assert.Contains("Do convite ao fim do vídeo", html);
        Assert.Contains("Ainda não assistiram", html);
        Assert.Contains("pendente@barcelos.dev", html);
    }

    [Fact]
    public async Task A_listagem_de_pessoas_mostra_quem_entrou()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var html = await cliente.GetStringAsync("/admin/pessoas");

        Assert.Contains(Admin, html);
        Assert.Contains("Administrador", html);
    }

    [Fact]
    public async Task A_busca_de_pessoas_filtra_pelo_email()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        Assert.Contains(Admin, await cliente.GetStringAsync("/admin/pessoas?q=barcelos"));
        Assert.Contains("Nenhuma pessoa corresponde", await cliente.GetStringAsync("/admin/pessoas?q=ninguem"));
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

        var html = await cliente.GetStringAsync($"/admin/pessoas/{usuarioId}");

        Assert.Contains("Boas-vindas", html);
        Assert.Contains("até o fim", html);
    }

    [Fact]
    public async Task Pessoa_inexistente_responde_como_nao_encontrada()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var resposta = await cliente.GetAsync($"/admin/pessoas/{Guid.CreateVersion7()}");

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

        var resposta = await cliente.GetAsync($"/admin/exportar/videos/{video.Id}/espectadores.csv");
        var csv = await resposta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("text/csv", resposta.Content.Headers.ContentType!.MediaType);
        Assert.Contains("reuniao-trimestral", resposta.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Contains("Pessoa;Sessões", csv);
        Assert.Contains("sim", csv);
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

        var resposta = await cliente.GetAsync($"/admin/exportar/pessoas/{usuarioId}/atividade.csv");
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
        var resposta = await cliente.GetAsync($"/admin/exportar/videos/{video.Id}/espectadores.csv");

        Assert.Equal(HttpStatusCode.Found, resposta.StatusCode);
        Assert.Contains("/entrar", resposta.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Exportar_video_inexistente_responde_como_nao_encontrado()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var resposta = await cliente.GetAsync($"/admin/exportar/videos/{Guid.CreateVersion7()}/espectadores.csv");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    private sealed record SessaoResposta(Guid SessaoId);
}
