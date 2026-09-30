// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using System.Net.Http.Headers;
using OpenTube.Domain.Enums;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Paginas;

/// <summary>
/// Miniatura opcional da coleção. Sem imagem, o cartão continua com o nome; com imagem, a
/// capa passa a ser o arquivo, e só quem vê a coleção recebe o endereço.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class MiniaturaDaColecaoTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
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

    [Fact]
    public async Task A_capa_enviada_substitui_o_nome_e_remover_devolve_o_padrao()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Abertura", VideoVisibility.Public);

        using var admin = _app.CreateBrowser();
        await EntrarAsync(admin);
        var colecao = await CriarColecaoAsync(admin, "Treinamentos");

        await FormularioHelpers.EnviarFormularioAsync(
            admin, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/add",
            new Dictionary<string, string> { ["videoId"] = video.Id.ToString() });

        using var visitante = _app.CreateBrowser();
        var antes = await visitante.GetStringAsync("/");
        Assert.Contains("collection-cover", antes);
        Assert.DoesNotContain($"/api/collections/{colecao}/thumbnail", antes);

        var envio = await EnviarAsync(admin, colecao, PngDeTeste.Criar(640, 360));
        Assert.StartsWith($"/admin/collections/{colecao}?capa=1", envio.Headers.Location!.ToString());

        var pagina = await admin.GetStringAsync($"/admin/collections/{colecao}?capa=1");
        Assert.Contains("Thumbnail saved.", pagina);
        Assert.Contains($"/api/collections/{colecao}/thumbnail?v=", pagina);

        var home = await visitante.GetStringAsync("/");
        Assert.Contains($"/api/collections/{colecao}/thumbnail?v=", home);
        Assert.DoesNotContain("collection-cover", home);

        var capa = await visitante.GetAsync($"/api/collections/{colecao}/thumbnail");
        Assert.Equal(HttpStatusCode.Found, capa.StatusCode);
        Assert.Contains("X-Amz-Signature", capa.Headers.Location!.ToString());
        Assert.Equal("no-store", capa.Headers.CacheControl!.ToString());

        using var direto = new HttpClient();
        var bytes = await direto.GetByteArrayAsync(capa.Headers.Location);
        Assert.Equal([0xFF, 0xD8, 0xFF], bytes[..3]);

        var remocao = await FormularioHelpers.EnviarFormularioAsync(
            admin, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/thumbnail/remove",
            new Dictionary<string, string>());
        Assert.StartsWith($"/admin/collections/{colecao}?semcapa=1", remocao.Headers.Location!.ToString());

        var deNovo = await visitante.GetStringAsync("/");
        Assert.Contains("collection-cover", deNovo);
        Assert.DoesNotContain($"/api/collections/{colecao}/thumbnail", deNovo);
        Assert.Equal(HttpStatusCode.NotFound, (await visitante.GetAsync($"/api/collections/{colecao}/thumbnail")).StatusCode);
    }

    [Fact]
    public async Task Quem_nao_ve_a_colecao_nao_recebe_a_capa()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Sigiloso", VideoVisibility.Private);

        using var admin = _app.CreateBrowser();
        await EntrarAsync(admin);
        var colecao = await CriarColecaoAsync(admin, "Interna");

        await FormularioHelpers.EnviarFormularioAsync(
            admin, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/add",
            new Dictionary<string, string> { ["videoId"] = video.Id.ToString() });
        await EnviarAsync(admin, colecao, PngDeTeste.Criar(400, 200));

        using var visitante = _app.CreateBrowser();
        Assert.Equal(HttpStatusCode.NotFound, (await visitante.GetAsync($"/api/collections/{colecao}/thumbnail")).StatusCode);
        Assert.DoesNotContain($"/api/collections/{colecao}/thumbnail", await visitante.GetStringAsync("/"));

        var doAdmin = await admin.GetAsync($"/api/collections/{colecao}/thumbnail");
        Assert.Equal(HttpStatusCode.Found, doAdmin.StatusCode);
    }

    [Fact]
    public async Task Arquivo_que_nao_e_imagem_volta_com_o_motivo()
    {
        using var admin = _app.CreateBrowser();
        await EntrarAsync(admin);
        var colecao = await CriarColecaoAsync(admin, "Treinamentos");

        var resposta = await EnviarAsync(admin, colecao, "não é imagem"u8.ToArray());

        Assert.Contains("erro=", resposta.Headers.Location!.ToString());
        var pagina = await admin.GetStringAsync(resposta.Headers.Location!.ToString());
        Assert.Contains("The file is not a JPEG, PNG or WebP image.", pagina);
    }

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

    private static async Task<Guid> CriarColecaoAsync(HttpClient cliente, string nome)
    {
        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/admin/collections", "/admin/collections/create",
            new Dictionary<string, string> { ["nome"] = nome, ["descricao"] = "" });

        return Guid.Parse(resposta.Headers.Location!.ToString().Split('/')[^1].Split('?')[0]);
    }

    private static async Task<HttpResponseMessage> EnviarAsync(HttpClient cliente, Guid colecao, byte[] arquivo)
    {
        var token = await FormularioHelpers.TokenAntifalsificacaoAsync(cliente, $"/admin/collections/{colecao}");

        using var conteudo = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" }
        };

        var bytes = new ByteArrayContent(arquivo);
        bytes.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        conteudo.Add(bytes, "arquivo", "capa.png");

        return await cliente.PostAsync($"/admin/collections/{colecao}/thumbnail", conteudo);
    }
}
