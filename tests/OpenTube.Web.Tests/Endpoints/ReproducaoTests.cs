// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using OpenTube.Domain.Enums;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Endpoints;

/// <summary>
/// Autorização da entrega do vídeo. É o ponto em que um descuido publica conteúdo
/// confidencial, então cada caminho é exercitado pela interface de rede de verdade.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class ReproducaoTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
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

    [Fact]
    public async Task A_playlist_de_video_publico_aponta_de_volta_para_a_aplicacao()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var resposta = await cliente.GetAsync($"/api/videos/{video.Id}/master.m3u8");
        var conteudo = await resposta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("application/vnd.apple.mpegurl", resposta.Content.Headers.ContentType!.MediaType);
        Assert.Contains($"/api/videos/{video.Id}/renditions/360p.m3u8", conteudo);
        // O endereço interno do storage não pode aparecer na playlist principal.
        Assert.DoesNotContain("X-Amz-Signature", conteudo);
    }

    [Fact]
    public async Task A_playlist_da_versao_entrega_segmentos_assinados()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var conteudo = await cliente.GetStringAsync($"/api/videos/{video.Id}/renditions/360p.m3u8");

        Assert.Contains("X-Amz-Signature", conteudo);
        Assert.Contains("seg-00000.m4s?", conteudo);
        Assert.Contains("init-360p.mp4?", conteudo);
    }

    [Fact]
    public async Task Video_privado_responde_como_inexistente_a_quem_nao_tem_acesso()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Private);

        using var cliente = _app.CreateBrowser();

        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync($"/api/videos/{video.Id}/master.m3u8")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync($"/api/videos/{video.Id}/renditions/360p.m3u8")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync($"/api/videos/{video.Id}/thumbnail")).StatusCode);
    }

    [Fact]
    public async Task Pedir_a_versao_direto_nao_contorna_a_playlist_principal()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Restrito", VideoVisibility.Restricted);

        using var cliente = _app.CreateBrowser();

        // Copiar o endereço da versão é o atalho mais óbvio; ele precisa ser barrado igual.
        var resposta = await cliente.GetAsync($"/api/videos/{video.Id}/renditions/360p.m3u8");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Fact]
    public async Task O_administrador_reproduz_video_privado()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Private);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var resposta = await cliente.GetAsync($"/api/videos/{video.Id}/master.m3u8");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task Sair_corta_a_reproducao_do_conteudo_privado()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Private);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync($"/api/videos/{video.Id}/master.m3u8")).StatusCode);

        await FormularioHelpers.EnviarFormularioAsync(cliente, "/", "/sign-out", new Dictionary<string, string>());

        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync($"/api/videos/{video.Id}/master.m3u8")).StatusCode);
    }

    [Fact]
    public async Task A_miniatura_de_video_publico_redireciona_para_o_storage()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var resposta = await cliente.GetAsync($"/api/videos/{video.Id}/thumbnail");

        Assert.Equal(HttpStatusCode.Found, resposta.StatusCode);
        Assert.Contains("X-Amz-Signature", resposta.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Video_inexistente_responde_igual_a_video_sem_acesso()
    {
        using var cliente = _app.CreateBrowser();

        var resposta = await cliente.GetAsync($"/api/videos/{Guid.CreateVersion7()}/master.m3u8");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Fact]
    public async Task Versao_inventada_nao_entrega_nada()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var resposta = await cliente.GetAsync($"/api/videos/{video.Id}/renditions/4320p.m3u8");

        Assert.False(resposta.IsSuccessStatusCode);
    }
}
