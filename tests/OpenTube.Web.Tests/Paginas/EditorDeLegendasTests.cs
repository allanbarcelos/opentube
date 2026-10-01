// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTube.Domain.Captions;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Services;
using OpenTube.Infrastructure.Storage;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Paginas;

/// <summary>
/// Aba de legendas, pedido de transcrição por idioma e editor: situação de cada legenda,
/// recusa de pedido duplicado, edição, download e acesso restrito à administração.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class EditorDeLegendasTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private const string Admin = "allan@barcelos.dev";

    private const string Vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nBom dia a todos.\n\n00:00:04.000 --> 00:00:06.500\nVamos começar.\n";

    private OpenTubeWebFactory _app = default!;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        await WhisperDeTeste.InformarAsync(postgres);
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

    /// <summary>Vídeo com o original no storage, pronto para transcrever.</summary>
    private async Task<Video> VideoAsync()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);
        await storage.PutTextAsync(StorageBucket.Originals, video.OriginalKey, "arquivo", MediaTypes.Mp4);

        return video;
    }

    private async Task<VideoAsset> LegendaAsync(Guid videoId, string idioma = "pt-br", string conteudo = Vtt)
    {
        using var escopo = _app.Services.CreateScope();
        return await escopo.ServiceProvider.GetRequiredService<CaptionService>().UploadAsync(videoId, idioma, null, conteudo);
    }

    private static Task<HttpResponseMessage> PedirTranscricaoAsync(HttpClient cliente, Guid videoId, string idioma) =>
        FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{videoId}?tab=captions", $"/admin/videos/{videoId}/captions/transcribe",
            new Dictionary<string, string> { ["idioma"] = idioma });

    /// <summary>Grava pelo editor, como o script faz: JSON com o token no cabeçalho.</summary>
    private static async Task<HttpResponseMessage> SalvarNoEditorAsync(HttpClient cliente, Guid videoId, Guid legendaId, string conteudo)
    {
        var token = await FormularioHelpers.TokenAntifalsificacaoAsync(cliente, $"/admin/videos/{videoId}/captions/{legendaId}/edit");

        using var pedido = new HttpRequestMessage(HttpMethod.Post, $"/admin/videos/{videoId}/captions/{legendaId}/content")
        {
            Content = JsonContent.Create(new { conteudo })
        };
        pedido.Headers.Add("RequestVerificationToken", token);

        return await cliente.SendAsync(pedido);
    }

    [Fact]
    public async Task A_aba_de_legendas_mostra_a_situacao_de_cada_idioma()
    {
        var video = await VideoAsync();
        await LegendaAsync(video.Id, "en");

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        await PedirTranscricaoAsync(cliente, video.Id, "pt-BR");

        var html = await cliente.GetStringAsync($"/admin/videos/{video.Id}?tab=captions");

        Assert.Contains("data-legendas-processando=\"true\"", html);
        Assert.Contains("data-status=\"processing\"", html);
        Assert.Contains("data-status=\"ready\"", html);
        Assert.Contains("Português (Brasil)", html);
        Assert.Contains("English", html);

        // A legenda em processamento não pode ser editada nem pedida de novo; a pronta, sim.
        var processando = Regex.Match(html, "<tr data-legenda=\"[^\"]+\" data-status=\"processing\">(.*?)</tr>", RegexOptions.Singleline).Value;
        Assert.DoesNotContain("/edit", processando);
        Assert.DoesNotContain("captions/transcribe", processando);

        var pronta = Regex.Match(html, "<tr data-legenda=\"[^\"]+\" data-status=\"ready\">(.*?)</tr>", RegexOptions.Singleline).Value;
        Assert.Contains("/edit", pronta);
        Assert.Contains("/download", pronta);
    }

    [Fact]
    public async Task A_aba_configuracao_nao_tem_mais_as_legendas()
    {
        var video = await VideoAsync();

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var configuracao = await cliente.GetStringAsync($"/admin/videos/{video.Id}");

        Assert.Contains($"href=\"/admin/videos/{video.Id}?tab=captions\"", configuracao);
        Assert.DoesNotContain("captions/transcribe", configuracao);
    }

    [Fact]
    public async Task Pedido_repetido_do_mesmo_idioma_e_recusado_com_o_motivo()
    {
        var video = await VideoAsync();

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var primeiro = await PedirTranscricaoAsync(cliente, video.Id, "pt-BR");
        var segundo = await PedirTranscricaoAsync(cliente, video.Id, "pt-br");

        Assert.Contains("legenda=pedida", primeiro.Headers.Location!.ToString());
        Assert.Contains("already in progress", await cliente.GetStringAsync(segundo.Headers.Location!.ToString()));

        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.ProcessingJobs.CountAsync(j => j.Kind == JobKind.Transcript));
    }

    [Fact]
    public async Task A_situacao_e_consultada_em_json()
    {
        var video = await VideoAsync();

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        await PedirTranscricaoAsync(cliente, video.Id, "pt-BR");

        var lista = await cliente.GetFromJsonAsync<List<SituacaoJson>>($"/admin/videos/{video.Id}/captions/status");

        Assert.Equal("pt-br", Assert.Single(lista!).Idioma);
        Assert.Equal("processing", lista![0].Status);
    }

    private sealed record SituacaoJson(Guid Id, string Idioma, string Status);

    [Fact]
    public async Task O_editor_mostra_numero_tempo_e_texto_de_cada_trecho()
    {
        var video = await VideoAsync();
        var legenda = await LegendaAsync(video.Id);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var html = await cliente.GetStringAsync($"/admin/videos/{video.Id}/captions/{legenda.Id}/edit");

        // A linha-modelo, usada pelo script para inserir trechos, fica num <template> no fim.
        var grade = html[..html.IndexOf("editor-modelo-linha", StringComparison.Ordinal)];
        Assert.Equal(2, Regex.Matches(grade, "<div class=\"editor-linha\" role=\"row\"").Count);
        Assert.Contains(">1</button>", html);
        Assert.Contains(">2</button>", html);
        Assert.Contains("value=\"00:00:01.000\"", html);
        Assert.Contains("value=\"00:00:06.500\"", html);
        Assert.Contains(">Vamos começar.</textarea>", html);
        Assert.Contains($"data-salvar=\"/admin/videos/{video.Id}/captions/{legenda.Id}/content\"", html);
        Assert.Contains($"data-manifest=\"/api/videos/{video.Id}/master.m3u8?t=", html);
    }

    [Fact]
    public async Task Salvar_no_editor_grava_a_versao_corrigida()
    {
        var video = await VideoAsync();
        var legenda = await LegendaAsync(video.Id);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var resposta = await SalvarNoEditorAsync(cliente, video.Id, legenda.Id,
            "WEBVTT\n\n00:00:01.000 --> 00:00:02.500\nBom dia a todas.\n");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        using var storage = minio.CreateStorage();
        var conteudo = await storage.GetTextAsync(StorageBucket.Vod, legenda.StorageKey);
        Assert.Equal("Bom dia a todas.", CaptionDocument.Parse(conteudo).Cues.Single().Text);

        await using var db = postgres.CreateContext();
        Assert.Equal(CaptionSource.Edited, (await db.VideoAssets.SingleAsync()).Source);
    }

    [Fact]
    public async Task Edicao_invalida_volta_com_o_motivo_sem_perder_o_conteudo_salvo()
    {
        var video = await VideoAsync();
        var legenda = await LegendaAsync(video.Id);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var resposta = await SalvarNoEditorAsync(cliente, video.Id, legenda.Id,
            "WEBVTT\n\n00:00:05.000 --> 00:00:04.000\nAo contrário\n");

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Contains("Cue 1: the end time must come after the start time.", await resposta.Content.ReadAsStringAsync());

        using var storage = minio.CreateStorage();
        Assert.Contains("Bom dia a todos.", await storage.GetTextAsync(StorageBucket.Vod, legenda.StorageKey));
    }

    [Fact]
    public async Task Salvar_sem_o_token_e_recusado()
    {
        var video = await VideoAsync();
        var legenda = await LegendaAsync(video.Id);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var resposta = await cliente.PostAsJsonAsync(
            $"/admin/videos/{video.Id}/captions/{legenda.Id}/content", new { conteudo = "WEBVTT\n" });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);

        using var storage = minio.CreateStorage();
        Assert.Contains("Bom dia a todos.", await storage.GetTextAsync(StorageBucket.Vod, legenda.StorageKey));
    }

    [Fact]
    public async Task Legenda_em_processamento_nao_abre_para_edicao()
    {
        var video = await VideoAsync();
        var legenda = await LegendaAsync(video.Id);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        await PedirTranscricaoAsync(cliente, video.Id, "pt-br");

        var html = await cliente.GetStringAsync($"/admin/videos/{video.Id}/captions/{legenda.Id}/edit");
        Assert.Contains("This caption is being generated.", html);
        Assert.DoesNotContain("editor-linha", html);

        var resposta = await SalvarNoEditorAsync(cliente, video.Id, legenda.Id, Vtt);
        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Contains("Wait for the transcription", await resposta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Download_entrega_o_arquivo_com_nome_do_video_e_do_idioma()
    {
        var video = await VideoAsync();
        var legenda = await LegendaAsync(video.Id, "en");

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var resposta = await cliente.GetAsync($"/admin/videos/{video.Id}/captions/{legenda.Id}/download");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("text/vtt", resposta.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"{video.Slug}.en.vtt", resposta.Content.Headers.ContentDisposition!.FileNameStar ?? resposta.Content.Headers.ContentDisposition.FileName);
        Assert.Equal(2, CaptionDocument.Parse(await resposta.Content.ReadAsStringAsync()).Cues.Count);
    }

    [Fact]
    public async Task Legenda_ainda_sem_conteudo_nao_vai_para_o_player()
    {
        var video = await VideoAsync();

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        await PedirTranscricaoAsync(cliente, video.Id, "pt-BR");

        var pagina = await cliente.GetStringAsync($"/watch/{video.Slug}");

        Assert.DoesNotContain("<track", pagina);
    }

    [Fact]
    public async Task Quem_nao_e_administrador_nao_edita_nem_baixa()
    {
        var video = await VideoAsync();
        var legenda = await LegendaAsync(video.Id);

        using var visitante = _app.CreateBrowser();

        var editor = await visitante.GetAsync($"/admin/videos/{video.Id}/captions/{legenda.Id}/edit");
        var download = await visitante.GetAsync($"/admin/videos/{video.Id}/captions/{legenda.Id}/download");
        var situacao = await visitante.GetAsync($"/admin/videos/{video.Id}/captions/status");
        var gravacao = await visitante.PostAsJsonAsync($"/admin/videos/{video.Id}/captions/{legenda.Id}/content", new { conteudo = "WEBVTT\n" });

        foreach (var resposta in new[] { editor, download, situacao, gravacao })
        {
            Assert.NotEqual(HttpStatusCode.OK, resposta.StatusCode);
            Assert.DoesNotContain("Bom dia", await resposta.Content.ReadAsStringAsync());
        }

        using var storage = minio.CreateStorage();
        Assert.Contains("Bom dia a todos.", await storage.GetTextAsync(StorageBucket.Vod, legenda.StorageKey));
    }

    [Fact]
    public async Task Com_whisper_disponivel_a_aba_oferece_a_deteccao_automatica()
    {
        var video = await VideoAsync();

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var html = await cliente.GetStringAsync($"/admin/videos/{video.Id}?tab=captions");

        Assert.Contains("data-transcricao=\"disponivel\"", html);
        Assert.Contains("<option value=\"auto\" selected", html);
        Assert.Contains("small-q5_1", html);
    }

    [Fact]
    public async Task Sem_whisper_a_aba_so_oferece_envio_e_escrita()
    {
        await WhisperDeTeste.InformarAsync(postgres, disponivel: false);
        var video = await VideoAsync();
        await LegendaAsync(video.Id);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var html = await cliente.GetStringAsync($"/admin/videos/{video.Id}?tab=captions");

        Assert.Contains("data-transcricao=\"indisponivel\"", html);
        Assert.DoesNotContain("captions/transcribe", html);
        Assert.Contains("captions/new", html);
        Assert.Contains($"/admin/videos/{video.Id}/captions\"", html);

        // E o pedido direto também é recusado, sem criar nada.
        var resposta = await PedirTranscricaoAsync(cliente, video.Id, "auto");
        Assert.Contains("erro=", resposta.Headers.Location!.OriginalString);

        await using var db = postgres.CreateContext();
        Assert.Equal(0, await db.ProcessingJobs.CountAsync(j => j.Kind == JobKind.Transcript));
    }

    [Fact]
    public async Task Pedido_com_deteccao_automatica_mostra_a_legenda_provisoria()
    {
        var video = await VideoAsync();

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        await PedirTranscricaoAsync(cliente, video.Id, "auto");

        var html = await cliente.GetStringAsync($"/admin/videos/{video.Id}?tab=captions");

        Assert.Contains("Detecting the spoken language", html);
        var processando = Regex.Match(html, "<tr data-legenda=\"[^\"]+\" data-status=\"processing\">(.*?)</tr>", RegexOptions.Singleline).Value;
        Assert.DoesNotContain("/edit", processando);
    }

    [Fact]
    public async Task Nova_legenda_abre_o_editor_vazio()
    {
        await WhisperDeTeste.InformarAsync(postgres, disponivel: false);
        var video = await VideoAsync();

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}?tab=captions", $"/admin/videos/{video.Id}/captions/new",
            new Dictionary<string, string> { ["idioma"] = "es", ["rotulo"] = "Español" });

        var destino = resposta.Headers.Location!.OriginalString;
        Assert.Matches($"/admin/videos/{video.Id}/captions/[0-9a-f-]+/edit$", destino);

        var editor = await cliente.GetAsync(destino);
        Assert.Equal(HttpStatusCode.OK, editor.StatusCode);

        // O mesmo idioma de novo é recusado com o motivo.
        var repetida = await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}?tab=captions", $"/admin/videos/{video.Id}/captions/new",
            new Dictionary<string, string> { ["idioma"] = "ES" });

        Assert.Contains("erro=", repetida.Headers.Location!.OriginalString);
    }
}
