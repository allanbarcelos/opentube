// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Enums;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Paginas;

/// <summary>
/// Sumário do vídeo: montado na administração, mostrado ao lado do vídeo e na barra de
/// capítulos. Os vídeos de teste têm 2:05.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class SumarioTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
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

    private async Task EntrarAsync(HttpClient cliente)
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

    private async Task<OpenTube.Domain.Entities.Video> VideoAsync()
    {
        using var storage = minio.CreateStorage();
        return await AcervoDeTeste.PublicarAsync(postgres, storage, "Reunião Trimestral", VideoVisibility.Public);
    }

    /// <summary>Salva o sumário como o editor faz: campos "inicio" e "titulo" repetidos, na ordem da tela.</summary>
    private static async Task<HttpResponseMessage> SalvarAsync(HttpClient cliente, Guid videoId, params (string Inicio, string Titulo)[] linhas)
    {
        var token = await FormularioHelpers.TokenAntifalsificacaoAsync(cliente, $"/admin/videos/{videoId}");
        var campos = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        foreach (var (inicio, titulo) in linhas)
        {
            campos.Add(new("inicio", inicio));
            campos.Add(new("titulo", titulo));
        }

        return await cliente.PostAsync($"/admin/videos/{videoId}/chapters", new FormUrlEncodedContent(campos));
    }

    [Fact]
    public async Task A_administracao_monta_o_sumario_e_ele_aparece_ao_lado_do_video()
    {
        var video = await VideoAsync();

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente);

        var editor = await cliente.GetStringAsync($"/admin/videos/{video.Id}");
        Assert.Contains("data-sumario-editor", editor);
        Assert.Contains("template data-sumario-modelo", editor);

        var resposta = await SalvarAsync(cliente, video.Id, ("1:30", "Perguntas"), ("0:00", "Abertura"), ("", ""), ("0:40", "Resultados"));
        Assert.Equal(HttpStatusCode.Redirect, resposta.StatusCode);
        Assert.Contains("sumario=1", resposta.Headers.Location!.OriginalString);

        // O editor volta com as linhas salvas, já em ordem.
        var salvo = await cliente.GetStringAsync($"/admin/videos/{video.Id}?sumario=1");
        Assert.Contains("Chapters saved.", salvo);
        var ordem = new[] { "value=\"0:00\"", "value=\"0:40\"", "value=\"1:30\"" }.Select(v => salvo.IndexOf(v, StringComparison.Ordinal)).ToList();
        Assert.All(ordem, i => Assert.True(i > 0));
        Assert.Equal(ordem.Order(), ordem);

        var pagina = await cliente.GetStringAsync($"/watch/{video.Slug}");

        // Lista ao lado do vídeo, cada capítulo levando ao seu instante.
        Assert.Contains("data-sumario-lista", pagina);
        Assert.Contains($"href=\"/watch/{video.Slug}?t=40\" data-instante=\"40\"", pagina);
        Assert.Contains("Resultados", pagina);

        // Barra de progresso dos controles do player: um pedaço por capítulo, até o início do
        // seguinte, e o último até o fim do vídeo (2:05).
        Assert.Contains("data-controles", pagina);
        Assert.Equal(
            [(0, 40, "Abertura"), (40, 90, "Resultados"), (90, 125, "Perguntas")],
            CapitulosDaBarra(pagina));

        await using var db = postgres.CreateContext();
        Assert.True(await db.AuditEntries.AnyAsync(a => a.EntityId == video.Id && a.Summary!.Contains("3 chapter")));
    }

    [Fact]
    public async Task Linha_invalida_volta_com_o_motivo_e_nao_muda_o_sumario()
    {
        var video = await VideoAsync();

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente);
        await SalvarAsync(cliente, video.Id, ("0:00", "Abertura"));

        var resposta = await SalvarAsync(cliente, video.Id, ("0:00", "Abertura"), ("3:00", "Depois do fim"));
        var destino = resposta.Headers.Location!.OriginalString;
        Assert.Contains("erro-sumario=", destino);

        var pagina = await cliente.GetStringAsync(destino);
        Assert.Contains("Chapter 2: 3:00 is past the end of the video (2:05).", WebUtility.HtmlDecode(pagina));

        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.VideoChapters.CountAsync(c => c.VideoId == video.Id));
    }

    [Fact]
    public async Task Capitulo_que_nao_comeca_no_zero_deixa_um_trecho_sem_titulo_na_barra()
    {
        var video = await VideoAsync();

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente);
        await SalvarAsync(cliente, video.Id, ("0:30", "Depois da vinheta"));

        var pagina = await cliente.GetStringAsync($"/watch/{video.Slug}");

        Assert.Equal([(0, 30, ""), (30, 125, "Depois da vinheta")], CapitulosDaBarra(pagina));
    }

    [Fact]
    public async Task Video_sem_sumario_nao_mostra_lista_nem_barra()
    {
        var video = await VideoAsync();

        using var cliente = _app.CreateBrowser();

        var pagina = await cliente.GetStringAsync($"/watch/{video.Slug}");

        Assert.Empty(CapitulosDaBarra(pagina));
        Assert.DoesNotContain("data-sumario-lista", pagina);
    }

    /// <summary>Capítulos que a página entrega aos controles do player (data-capitulos).</summary>
    private static List<(int Inicio, int Fim, string Titulo)> CapitulosDaBarra(string pagina)
    {
        var atributo = System.Text.RegularExpressions.Regex.Match(pagina, "data-capitulos=\"([^\"]*)\"");
        Assert.True(atributo.Success, "a página não entrega os capítulos ao player");

        using var json = System.Text.Json.JsonDocument.Parse(WebUtility.HtmlDecode(atributo.Groups[1].Value));
        return json.RootElement.EnumerateArray()
            .Select(c => (c.GetProperty("inicio").GetInt32(), c.GetProperty("fim").GetInt32(), c.GetProperty("titulo").GetString()!))
            .ToList();
    }

    [Fact]
    public async Task So_a_administracao_salva_o_sumario()
    {
        var video = await VideoAsync();

        using var anonimo = _app.CreateBrowser();
        var resposta = await anonimo.PostAsync($"/admin/videos/{video.Id}/chapters",
            new FormUrlEncodedContent([new("inicio", "0:00"), new("titulo", "Intrusa")]));

        Assert.NotEqual(HttpStatusCode.OK, resposta.StatusCode);
        Assert.DoesNotContain("sumario=1", resposta.Headers.Location?.OriginalString ?? string.Empty);

        await using var db = postgres.CreateContext();
        Assert.Equal(0, await db.VideoChapters.CountAsync());
    }
}
