// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Storage;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Paginas;

/// <summary>Área administrativa: quem entra, o que dá para fazer e o que fica barrado.</summary>
[Collection(IntegrationCollection.Name)]
public class AdministracaoTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private const string Admin = "allan@barcelos.dev";
    private const string Convidado = "convidado@barcelos.dev";

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
        _app.Emails.Clear();

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/sign-in", "/sign-in/code", new Dictionary<string, string> { ["email"] = email });

        await FormularioHelpers.EnviarFormularioAsync(
            cliente,
            $"/sign-in?email={Uri.EscapeDataString(email)}&enviado=1",
            "/sign-in/verify",
            new Dictionary<string, string> { ["email"] = email, ["codigo"] = _app.Emails.LastCode() });
    }

    private async Task CriarConvidadoAsync()
    {
        await using var db = postgres.CreateContext();
        db.Users.Add(OpenTube.Domain.Entities.User.Create(
            OpenTube.Domain.ValueObjects.EmailAddress.Parse(Convidado), DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Visitante_anonimo_e_mandado_para_a_tela_de_entrada()
    {
        using var cliente = _app.CreateBrowser();

        var resposta = await cliente.GetAsync("/admin");

        Assert.Equal(HttpStatusCode.Found, resposta.StatusCode);
        Assert.Contains("/sign-in", resposta.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Convidado_autenticado_nao_entra_na_administracao()
    {
        await CriarConvidadoAsync();
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Convidado);

        var resposta = await cliente.GetAsync("/admin");

        Assert.NotEqual(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task Convidado_nao_consegue_iniciar_um_envio()
    {
        await CriarConvidadoAsync();
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Convidado);

        var resposta = await cliente.PostAsJsonAsync("/api/admin/uploads/start", new
        {
            titulo = "Invasão",
            arquivo = "a.mp4",
            tipo = "video/mp4",
            tamanho = 1024
        });

        Assert.NotEqual(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task O_painel_mostra_o_acervo_e_os_indicadores()
    {
        using var storage = minio.CreateStorage();
        await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Private);

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        var html = await cliente.GetStringAsync("/admin");

        Assert.Contains("Plano Confidencial", html);
        Assert.Contains("Videos in the library", html);
        Assert.Contains("Upload video", html);
    }

    [Fact]
    public async Task Envio_completo_pelo_navegador_cria_o_video_e_enfileira_o_processamento()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        var token = await FormularioHelpers.TokenAntifalsificacaoAsync(cliente, "/admin/upload");
        cliente.DefaultRequestHeaders.Add("RequestVerificationToken", token);

        var bilhete = await (await cliente.PostAsJsonAsync("/api/admin/uploads/start", new
        {
            titulo = "Reunião Trimestral",
            descricao = "Resultados do trimestre",
            arquivo = "reuniao.mp4",
            tipo = "video/mp4",
            tamanho = 2048
        })).Content.ReadFromJsonAsync<BilheteResposta>();

        Assert.NotNull(bilhete);
        Assert.Equal(1, bilhete.TotalDePedacos);

        // O navegador envia os bytes direto ao storage, com a URL que a aplicação assinou.
        var dados = new byte[2048];
        Random.Shared.NextBytes(dados);

        using var direto = new HttpClient();
        var envio = await direto.PutAsync(bilhete.Partes[0].Url, new ByteArrayContent(dados));
        envio.EnsureSuccessStatusCode();

        var conclusao = await cliente.PostAsJsonAsync($"/api/admin/uploads/{bilhete.VideoId}/complete", new
        {
            uploadId = bilhete.UploadId,
            partes = new[] { new { numero = 1, eTag = envio.Headers.ETag!.Tag } }
        });

        conclusao.EnsureSuccessStatusCode();

        await using var db = postgres.CreateContext();
        var video = await db.Videos.SingleAsync();

        Assert.Equal("Reunião Trimestral", video.Title);
        Assert.Equal(VideoStatus.Uploaded, video.Status);
        // Todo vídeo nasce privado, mesmo enviado pelo administrador.
        Assert.Equal(VideoVisibility.Private, video.Visibility);
        Assert.Equal(2048, video.SizeBytes);

        Assert.Equal(1, await db.ProcessingJobs.CountAsync(j => j.Kind == JobKind.Transcode));

        using var storage = minio.CreateStorage();
        Assert.True(await storage.ExistsAsync(StorageBucket.Originals, video.OriginalKey));
    }

    [Fact]
    public async Task Envio_sem_a_credencial_de_seguranca_e_recusado()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        var resposta = await cliente.PostAsJsonAsync("/api/admin/uploads/start", new
        {
            titulo = "Sem credencial",
            arquivo = "a.mp4",
            tipo = "video/mp4",
            tamanho = 1024
        });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Arquivo_que_nao_e_video_e_recusado_antes_do_envio()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        var token = await FormularioHelpers.TokenAntifalsificacaoAsync(cliente, "/admin/upload");
        cliente.DefaultRequestHeaders.Add("RequestVerificationToken", token);

        var resposta = await cliente.PostAsJsonAsync("/api/admin/uploads/start", new
        {
            titulo = "Planilha",
            arquivo = "orcamento.xlsx",
            tipo = "application/vnd.ms-excel",
            tamanho = 1024
        });

        Assert.False(resposta.IsSuccessStatusCode);

        await using var db = postgres.CreateContext();
        Assert.Empty(await db.Videos.ToListAsync());
    }

    [Fact]
    public async Task Salvar_altera_dados_e_libera_o_video()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Rascunho de título", VideoVisibility.Private);

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente,
            $"/admin/videos/{video.Id}",
            $"/admin/videos/{video.Id}/save",
            new Dictionary<string, string>
            {
                ["titulo"] = "Reunião Trimestral",
                ["descricao"] = "Resultados",
                ["etiquetas"] = "financeiro, trimestre",
                ["visibilidade"] = ((int)VideoVisibility.Public).ToString()
            });

        Assert.Equal(HttpStatusCode.Found, resposta.StatusCode);

        await using var db = postgres.CreateContext();
        var atualizado = await db.Videos.SingleAsync(v => v.Id == video.Id);

        Assert.Equal("Reunião Trimestral", atualizado.Title);
        Assert.Equal(VideoVisibility.Public, atualizado.Visibility);
        Assert.Equal(["financeiro", "trimestre"], atualizado.Tags);
    }

    [Fact]
    public async Task Excluir_tira_o_video_do_acervo_publico()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", $"/admin/videos/{video.Id}/delete", new Dictionary<string, string>());

        using var visitante = _app.CreateBrowser();

        Assert.DoesNotContain("Boas-vindas", await visitante.GetStringAsync("/"));
        Assert.Equal(HttpStatusCode.NotFound, (await visitante.GetAsync($"/api/videos/{video.Id}/master.m3u8")).StatusCode);
    }

    [Fact]
    public async Task Restaurar_devolve_o_video_ainda_privado()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", $"/admin/videos/{video.Id}/delete", new Dictionary<string, string>());

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", $"/admin/videos/{video.Id}/restore", new Dictionary<string, string>());

        await using var db = postgres.CreateContext();
        var restaurado = await db.Videos.SingleAsync(v => v.Id == video.Id);

        Assert.False(restaurado.IsDeleted);
        Assert.Equal(VideoVisibility.Private, restaurado.Visibility);
    }

    [Fact]
    public async Task Reprocessar_sem_o_original_mostra_o_motivo()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", $"/admin/videos/{video.Id}/reprocess", new Dictionary<string, string>());

        Assert.Contains("erro=", resposta.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_pagina_do_video_lista_as_tres_visibilidades()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Private);

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        var html = await cliente.GetStringAsync($"/admin/videos/{video.Id}");

        Assert.Contains("Private", html);
        Assert.Contains("Public", html);
        Assert.Contains("Restricted", html);
        Assert.Contains("Reprocess from the original", html);
    }

    [Fact]
    public async Task A_pagina_de_envio_aceita_varios_arquivos_e_uma_pasta()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        var html = await cliente.GetStringAsync("/admin/upload");

        Assert.Matches("id=\"arquivos\"[^>]*multiple", html);
        Assert.Matches("id=\"pasta\"[^>]*webkitdirectory", html);
        Assert.Contains("id=\"modelo-linha-envio\"", html);
        Assert.Contains("data-token=", html);
    }

    /// <summary>Envia um arquivo pela API, como o navegador faz, e devolve o vídeo criado.</summary>
    private static async Task<Guid> EnviarPelaApiAsync(HttpClient cliente, string? titulo, string arquivo, Guid? colecaoId)
    {
        var bilhete = await (await cliente.PostAsJsonAsync("/api/admin/uploads/start", new
        {
            titulo,
            arquivo,
            tipo = "video/mp4",
            tamanho = 512,
            colecaoId
        })).Content.ReadFromJsonAsync<BilheteResposta>();

        using var direto = new HttpClient();
        var envio = await direto.PutAsync(bilhete!.Partes[0].Url, new ByteArrayContent(new byte[512]));
        envio.EnsureSuccessStatusCode();

        var conclusao = await cliente.PostAsJsonAsync($"/api/admin/uploads/{bilhete.VideoId}/complete", new
        {
            uploadId = bilhete.UploadId,
            partes = new[] { new { numero = 1, eTag = envio.Headers.ETag!.Tag } },
            colecaoId
        });
        conclusao.EnsureSuccessStatusCode();

        return bilhete.VideoId;
    }

    [Fact]
    public async Task Envio_sem_titulo_usa_o_nome_do_arquivo()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);
        cliente.DefaultRequestHeaders.Add("RequestVerificationToken",
            await FormularioHelpers.TokenAntifalsificacaoAsync(cliente, "/admin/upload"));

        await EnviarPelaApiAsync(cliente, null, "Boas-vindas ao time.mp4", null);
        await EnviarPelaApiAsync(cliente, "", "Política de segurança.mov", null);

        await using var db = postgres.CreateContext();
        Assert.Equal(
            ["Boas-vindas ao time", "Política de segurança"],
            (await db.Videos.Select(v => v.Title).ToListAsync()).Order());
    }

    [Fact]
    public async Task Envio_de_pasta_cria_a_colecao_e_poe_os_videos_nela()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);
        cliente.DefaultRequestHeaders.Add("RequestVerificationToken",
            await FormularioHelpers.TokenAntifalsificacaoAsync(cliente, "/admin/upload"));

        var colecao = await (await cliente.PostAsJsonAsync("/api/admin/uploads/collection", new { nome = "Onboarding" }))
            .Content.ReadFromJsonAsync<ColecaoResposta>();

        Assert.NotNull(colecao);
        Assert.Equal("Onboarding", colecao.Nome);
        Assert.Equal($"/admin/collections/{colecao.ColecaoId}", colecao.Destino);

        var primeiro = await EnviarPelaApiAsync(cliente, null, "Onboarding/01-boas-vindas.mp4", colecao.ColecaoId);
        var segundo = await EnviarPelaApiAsync(cliente, null, "Onboarding/02-ferramentas.mp4", colecao.ColecaoId);

        await using var db = postgres.CreateContext();
        var videos = await db.Collections
            .Where(c => c.Id == colecao.ColecaoId)
            .SelectMany(c => c.Videos)
            .OrderBy(v => v.Position)
            .Select(v => v.VideoId)
            .ToListAsync();

        Assert.Equal([primeiro, segundo], videos);

        // A coleção aparece na administração e a criação fica na auditoria.
        var pagina = await cliente.GetStringAsync($"/admin/collections/{colecao.ColecaoId}");
        Assert.Contains("01-boas-vindas", pagina);
        Assert.Contains("02-ferramentas", pagina);
        Assert.True(await db.AuditEntries.AnyAsync(a => a.EntityId == colecao.ColecaoId));
    }

    [Fact]
    public async Task Colecao_sem_nome_e_recusada_com_o_motivo()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);
        cliente.DefaultRequestHeaders.Add("RequestVerificationToken",
            await FormularioHelpers.TokenAntifalsificacaoAsync(cliente, "/admin/upload"));

        var resposta = await cliente.PostAsJsonAsync("/api/admin/uploads/collection", new { nome = "  " });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Contains("Enter a name for the collection.", await resposta.Content.ReadAsStringAsync());

        await using var db = postgres.CreateContext();
        Assert.Empty(await db.Collections.ToListAsync());
    }

    [Fact]
    public async Task Envio_para_colecao_inexistente_e_recusado_antes_de_subir()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);
        cliente.DefaultRequestHeaders.Add("RequestVerificationToken",
            await FormularioHelpers.TokenAntifalsificacaoAsync(cliente, "/admin/upload"));

        var resposta = await cliente.PostAsJsonAsync("/api/admin/uploads/start", new
        {
            arquivo = "a.mp4",
            tipo = "video/mp4",
            tamanho = 512,
            colecaoId = Guid.CreateVersion7()
        });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Contains("The collection was not found.", await resposta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Convidado_nao_cria_colecao_pelo_envio()
    {
        await CriarConvidadoAsync();

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Convidado);

        var resposta = await cliente.PostAsJsonAsync("/api/admin/uploads/collection", new { nome = "Intrusa" });

        Assert.NotEqual(HttpStatusCode.OK, resposta.StatusCode);

        await using var db = postgres.CreateContext();
        Assert.Empty(await db.Collections.ToListAsync());
    }

    private sealed record ColecaoResposta(Guid ColecaoId, string Nome, string Destino);

    private sealed record ParteResposta(int Numero, string Url);

    private sealed record BilheteResposta(
        Guid VideoId, string UploadId, int TamanhoDoPedaco, int TotalDePedacos, ParteResposta[] Partes);
}
