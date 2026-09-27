// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTube.Domain.Enums;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Endpoints;

/// <summary>
/// Coleta do que o player relata: quem pode abrir sessão, o que é aceito e o que é barrado.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class ColetaDeAudienciaTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
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

    private async Task<Guid> AbrirSessaoAsync(HttpClient cliente, Guid videoId)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/playback/start", new { videoId });
        resposta.EnsureSuccessStatusCode();

        var corpo = await resposta.Content.ReadFromJsonAsync<SessaoResposta>();

        return corpo!.SessaoId;
    }

    private static object Progresso(double de, double ate) =>
        new { tipo = "progress", em = ate, de, ate };

    [Fact]
    public async Task Visitante_abre_sessao_em_video_publico()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var sessao = await AbrirSessaoAsync(cliente, video.Id);

        await using var db = postgres.CreateContext();
        var gravada = await db.PlaybackSessions.SingleAsync();

        Assert.Equal(sessao, gravada.Id);
        Assert.Null(gravada.UserId);
        Assert.False(string.IsNullOrWhiteSpace(gravada.AnonymousId));
    }

    [Fact]
    public async Task Video_sem_acesso_nao_abre_sessao()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Private);

        using var cliente = _app.CreateBrowser();
        var resposta = await cliente.PostAsJsonAsync("/api/playback/start", new { videoId = video.Id });

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);

        await using var db = postgres.CreateContext();
        Assert.Empty(await db.PlaybackSessions.ToListAsync());
    }

    [Fact]
    public async Task Video_inexistente_nao_abre_sessao()
    {
        using var cliente = _app.CreateBrowser();

        var resposta = await cliente.PostAsJsonAsync("/api/playback/start", new { videoId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Fact]
    public async Task Os_trechos_relatados_sao_gravados()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var sessao = await AbrirSessaoAsync(cliente, video.Id);

        var resposta = await cliente.PostAsJsonAsync($"/api/playback/{sessao}/events", new
        {
            eventos = new[] { Progresso(0, 30), Progresso(30, 62) }
        });

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);

        await using var db = postgres.CreateContext();
        var gravada = await db.PlaybackSessions.Include(s => s.Intervals).SingleAsync();

        Assert.Equal(62, gravada.WatchedSeconds);
        Assert.Single(gravada.Intervals);
    }

    [Fact]
    public async Task Outro_navegador_nao_relata_na_sessao_alheia()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var dono = _app.CreateBrowser();
        var sessao = await AbrirSessaoAsync(dono, video.Id);

        // Outro navegador não tem o cookie de visitante que abriu a sessão.
        using var intruso = _app.CreateBrowser();
        var resposta = await intruso.PostAsJsonAsync($"/api/playback/{sessao}/events", new
        {
            eventos = new[] { Progresso(0, 600) }
        });

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);

        await using var db = postgres.CreateContext();
        Assert.Equal(0, (await db.PlaybackSessions.SingleAsync()).WatchedSeconds);
    }

    [Fact]
    public async Task A_sessao_de_quem_entrou_fica_ligada_a_pessoa()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        _app.Emails.Clear();
        await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/sign-in", "/sign-in/code", new Dictionary<string, string> { ["email"] = Admin });
        await FormularioHelpers.EnviarFormularioAsync(
            cliente,
            $"/sign-in?email={Uri.EscapeDataString(Admin)}&enviado=1",
            "/sign-in/verify",
            new Dictionary<string, string> { ["email"] = Admin, ["codigo"] = _app.Emails.LastCode() });

        await AbrirSessaoAsync(cliente, video.Id);

        await using var db = postgres.CreateContext();
        var gravada = await db.PlaybackSessions.SingleAsync();
        var usuario = await db.Users.SingleAsync(u => u.Email == Admin);

        Assert.Equal(usuario.Id, gravada.UserId);
        Assert.Null(gravada.AnonymousId);
    }

    [Fact]
    public async Task O_mesmo_navegador_mantem_o_identificador_entre_sessoes()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        await AbrirSessaoAsync(cliente, video.Id);
        await AbrirSessaoAsync(cliente, video.Id);

        await using var db = postgres.CreateContext();
        var visitantes = await db.PlaybackSessions.Select(s => s.AnonymousId).Distinct().ToListAsync();

        Assert.Single(visitantes);
    }

    [Fact]
    public async Task Encerrar_fecha_a_sessao()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var sessao = await AbrirSessaoAsync(cliente, video.Id);

        var resposta = await cliente.PostAsync($"/api/playback/{sessao}/close", null);

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);

        await using var db = postgres.CreateContext();
        Assert.NotNull((await db.PlaybackSessions.SingleAsync()).EndedAt);
    }

    [Fact]
    public async Task O_link_secreto_tambem_registra_a_audiencia()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Restricted);

        using var escopo = _app.Services.CreateScope();
        var link = await escopo.ServiceProvider
            .GetRequiredService<OpenTube.Infrastructure.Access.GrantService>()
            .CreateShareLinkAsync(GrantTargetType.Video, video.Id,
                OpenTube.Infrastructure.Access.GrantValidity.Forever, Guid.CreateVersion7());

        using var visitante = _app.CreateBrowser();
        await visitante.GetAsync(new Uri(link.Url).PathAndQuery);

        await AbrirSessaoAsync(visitante, video.Id);

        await using var db = postgres.CreateContext();
        var gravada = await db.PlaybackSessions.SingleAsync();

        // A concessão fica registrada na sessão: é o que liga a audiência ao convite.
        Assert.Equal(link.GrantId, gravada.GrantId);
    }

    private sealed record SessaoResposta(Guid SessaoId);
}
