// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Enums;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Paginas;

/// <summary>
/// "Este vídeo foi útil?": quem entrou avalia de 1 a 5 e vê só a própria nota; o conjunto
/// aparece apenas para a administração.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AvaliacaoTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private const string Admin = "allan@barcelos.dev";
    private const string Pessoa = "convidado@barcelos.dev";

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
        await using (var db = postgres.CreateContext())
        {
            if (!await db.Users.AnyAsync(u => u.Email == email))
            {
                db.Users.Add(OpenTube.Domain.Entities.User.Create(
                    OpenTube.Domain.ValueObjects.EmailAddress.Parse(email), DateTimeOffset.UtcNow));
                await db.SaveChangesAsync();
            }
        }

        _app.Emails.Clear();

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/sign-in", "/sign-in/code", new Dictionary<string, string> { ["email"] = email });

        await FormularioHelpers.EnviarFormularioAsync(
            cliente,
            $"/sign-in?email={Uri.EscapeDataString(email)}&enviado=1",
            "/sign-in/verify",
            new Dictionary<string, string> { ["email"] = email, ["codigo"] = _app.Emails.LastCode() });
    }

    private async Task<OpenTube.Domain.Entities.Video> VideoAsync()
    {
        using var storage = minio.CreateStorage();
        return await AcervoDeTeste.PublicarAsync(postgres, storage, "Reunião Trimestral", VideoVisibility.Public);
    }

    /// <summary>Envia a nota como o avaliacao.js: formulário com o token, pedindo JSON de volta.</summary>
    private static async Task<HttpResponseMessage> AvaliarAsync(HttpClient cliente, OpenTube.Domain.Entities.Video video, int nota)
    {
        var token = await FormularioHelpers.TokenAntifalsificacaoAsync(cliente, $"/watch/{video.Slug}");
        using var pedido = new HttpRequestMessage(HttpMethod.Post, $"/videos/{video.Id}/rating")
        {
            Content = new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", token),
                new("nota", nota.ToString()),
                new("destino", $"/watch/{video.Slug}")
            ])
        };
        pedido.Headers.Accept.ParseAdd("application/json");

        return await cliente.SendAsync(pedido);
    }

    [Fact]
    public async Task Visitante_anonimo_nao_ve_a_avaliacao()
    {
        var video = await VideoAsync();

        using var anonimo = _app.CreateBrowser();

        Assert.DoesNotContain("data-avaliacao", await anonimo.GetStringAsync($"/watch/{video.Slug}"));
    }

    [Fact]
    public async Task Quem_entrou_avalia_sem_recarregar_e_ve_so_a_propria_nota()
    {
        var video = await VideoAsync();

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Pessoa);

        var antes = await cliente.GetStringAsync($"/watch/{video.Slug}");
        Assert.Contains("Was this video useful?", antes);
        Assert.Contains("Only the administration sees the ratings.", antes);

        var resposta = await AvaliarAsync(cliente, video, 4);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(4, (await resposta.Content.ReadFromJsonAsync<Nota>())!.nota);

        var depois = await cliente.GetStringAsync($"/watch/{video.Slug}");
        Assert.Contains("Your rating: 4 of 5. You can change it.", depois);
        Assert.Contains("value=\"4\" class=\"estrela marcada\" aria-pressed=\"true\"", depois);
        Assert.Matches("value=\"5\" class=\"estrela\\s*\" aria-pressed=\"false\"", depois);

        // Nada do conjunto aparece para quem assiste.
        Assert.DoesNotContain("rating(s)", depois);
        Assert.DoesNotContain("data-avaliacoes", depois);
    }

    [Fact]
    public async Task Sem_script_o_formulario_volta_para_a_pagina()
    {
        var video = await VideoAsync();

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Pessoa);

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/watch/{video.Slug}", $"/videos/{video.Id}/rating",
            new Dictionary<string, string> { ["nota"] = "2", ["destino"] = $"/watch/{video.Slug}" });

        Assert.Equal($"/watch/{video.Slug}?avaliado=1#avaliacao", resposta.Headers.Location!.OriginalString);

        await using var db = postgres.CreateContext();
        Assert.Equal(2, (await db.VideoRatings.SingleAsync()).Score);
    }

    [Fact]
    public async Task A_administracao_ve_a_media_e_a_distribuicao()
    {
        var video = await VideoAsync();

        foreach (var (email, nota) in new[] { ("a@barcelos.dev", 5), ("b@barcelos.dev", 4), ("c@barcelos.dev", 3) })
        {
            using var pessoa = _app.CreateBrowser();
            await EntrarAsync(pessoa, email);
            (await AvaliarAsync(pessoa, video, nota)).EnsureSuccessStatusCode();
        }

        using var admin = _app.CreateBrowser();
        await EntrarAsync(admin, Admin);

        var pagina = await admin.GetStringAsync($"/admin/videos/{video.Id}");

        Assert.Contains("data-avaliacoes", pagina);
        Assert.Contains("data-media>4.0</span>", pagina);
        Assert.Contains("3 rating(s)", pagina);
        Assert.Matches("data-nota=\"5\"[\\s\\S]*?width: 33%", pagina);
    }

    [Fact]
    public async Task Anonimo_nao_consegue_gravar_nota()
    {
        var video = await VideoAsync();

        using var anonimo = _app.CreateBrowser();
        var resposta = await anonimo.PostAsync($"/videos/{video.Id}/rating",
            new FormUrlEncodedContent([new("nota", "5")]));

        Assert.NotEqual(HttpStatusCode.OK, resposta.StatusCode);

        await using var db = postgres.CreateContext();
        Assert.Equal(0, await db.VideoRatings.CountAsync());
    }

    private sealed record Nota(int nota);
}
