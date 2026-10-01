// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using OpenTube.Domain.Access;
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
        var resposta = await cliente.GetAsync(await Reproducao.ManifestoDaPaginaAsync(cliente, video.Slug));
        var conteudo = await resposta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("application/vnd.apple.mpegurl", resposta.Content.Headers.ContentType!.MediaType);
        // A versão leva o token da reprodução: sem ele, a playlist dela não sai.
        Assert.Contains($"/api/videos/{video.Id}/renditions/360p.m3u8?t=", conteudo);
        // O endereço interno do storage não pode aparecer na playlist principal.
        Assert.DoesNotContain("X-Amz-Signature", conteudo);
    }

    [Fact]
    public async Task A_playlist_da_versao_entrega_segmentos_assinados()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var conteudo = await cliente.GetStringAsync(await Reproducao.VersaoPelaPaginaAsync(cliente, video.Slug, "360p"));

        Assert.Contains("X-Amz-Signature", conteudo);
        Assert.Contains("seg-00000.m4s?", conteudo);
        Assert.Contains("init-360p.mp4?", conteudo);
    }

    [Fact]
    public async Task Versao_inexistente_em_video_publico_responde_404_e_nao_500()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();

        // Qualquer anônimo pode chutar nomes de versão; um que não existe é "não encontrado",
        // nunca um erro do servidor que vaza stack e enche o log.
        foreach (var nome in new[] { "720p", "999p", "qualquercoisa" })
        {
            var resposta = await cliente.GetAsync(Reproducao.VersaoCom(_app, video.Id, nome, Viewer.Anonymous));
            Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        }
    }

    [Fact]
    public async Task Video_privado_responde_como_inexistente_a_quem_nao_tem_acesso()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Private);

        using var cliente = _app.CreateBrowser();

        // Mesmo com um token legítimo, o acesso continua sendo conferido.
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync(Reproducao.ManifestoCom(_app, video.Id, Viewer.Anonymous))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync(Reproducao.VersaoCom(_app, video.Id, "360p", Viewer.Anonymous))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync($"/api/videos/{video.Id}/thumbnail")).StatusCode);
    }

    [Fact]
    public async Task Pedir_a_versao_direto_nao_contorna_a_playlist_principal()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Restrito", VideoVisibility.Restricted);

        using var cliente = _app.CreateBrowser();

        // Copiar o endereço da versão é o atalho mais óbvio; ele precisa ser barrado igual.
        var resposta = await cliente.GetAsync(Reproducao.VersaoCom(_app, video.Id, "360p", Viewer.Anonymous));

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Fact]
    public async Task O_administrador_reproduz_video_privado()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Private);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        var resposta = await cliente.GetAsync(await Reproducao.ManifestoDaPaginaAsync(cliente, video.Slug));

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task Sair_corta_a_reproducao_do_conteudo_privado()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Private);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        var manifesto = await Reproducao.ManifestoDaPaginaAsync(cliente, video.Slug);
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync(manifesto)).StatusCode);

        await FormularioHelpers.EnviarFormularioAsync(cliente, "/", "/sign-out", new Dictionary<string, string>());

        // O token era de quem entrou; sem a sessão, nem ele nem um novo abrem o vídeo.
        Assert.False((await cliente.GetAsync(manifesto)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync(Reproducao.ManifestoCom(_app, video.Id, Viewer.Anonymous))).StatusCode);
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

        var resposta = await cliente.GetAsync(Reproducao.ManifestoCom(_app, Guid.CreateVersion7(), Viewer.Anonymous));

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Fact]
    public async Task Versao_inventada_nao_entrega_nada()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var resposta = await cliente.GetAsync(Reproducao.VersaoCom(_app, video.Id, "4320p", Viewer.Anonymous));

        Assert.False(resposta.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Sem_o_token_da_pagina_as_playlists_nao_saem()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();

        // O endereço do vídeo, sozinho, não abre nada — nem num vídeo público.
        Assert.Equal(HttpStatusCode.Forbidden, (await cliente.GetAsync($"/api/videos/{video.Id}/master.m3u8")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cliente.GetAsync($"/api/videos/{video.Id}/renditions/360p.m3u8")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cliente.GetAsync($"/api/videos/{video.Id}/master.m3u8?t=123.abc")).StatusCode);
    }

    [Fact]
    public async Task Abrir_o_endereco_do_video_direto_no_navegador_e_recusado()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var manifesto = await Reproducao.ManifestoDaPaginaAsync(cliente, video.Slug);

        // O que o navegador manda quando o endereço é colado na barra ou aberto numa aba.
        using var navegacao = new HttpRequestMessage(HttpMethod.Get, manifesto);
        navegacao.Headers.Add("Sec-Fetch-Mode", "navigate");
        navegacao.Headers.Add("Sec-Fetch-Dest", "document");
        navegacao.Headers.Add("Sec-Fetch-Site", "none");
        Assert.Equal(HttpStatusCode.Forbidden, (await cliente.SendAsync(navegacao)).StatusCode);

        // Outro site embutindo o vídeo.
        using var deFora = new HttpRequestMessage(HttpMethod.Get, manifesto);
        deFora.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await cliente.SendAsync(deFora)).StatusCode);

        // O player do site: o mesmo endereço, pedido pela página.
        using var player = new HttpRequestMessage(HttpMethod.Get, manifesto);
        player.Headers.Add("Sec-Fetch-Mode", "cors");
        player.Headers.Add("Sec-Fetch-Dest", "empty");
        player.Headers.Add("Sec-Fetch-Site", "same-origin");
        Assert.Equal(HttpStatusCode.OK, (await cliente.SendAsync(player)).StatusCode);
    }

    [Fact]
    public async Task O_token_de_um_video_nao_abre_outro()
    {
        using var storage = minio.CreateStorage();
        var primeiro = await AcervoDeTeste.PublicarAsync(postgres, storage, "Primeiro", VideoVisibility.Public);
        var segundo = await AcervoDeTeste.PublicarAsync(postgres, storage, "Segundo", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var manifesto = await Reproducao.ManifestoDaPaginaAsync(cliente, primeiro.Slug);
        var token = manifesto[(manifesto.IndexOf("?t=", StringComparison.Ordinal) + 3)..];

        Assert.Equal(HttpStatusCode.Forbidden,
            (await cliente.GetAsync($"/api/videos/{segundo.Id}/master.m3u8?t={token}")).StatusCode);
    }

    [Fact]
    public async Task O_token_da_pagina_nao_serve_de_token_da_reproducao()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var manifesto = await Reproducao.ManifestoDaPaginaAsync(cliente, video.Slug);
        var token = manifesto[(manifesto.IndexOf("?t=", StringComparison.Ordinal) + 3)..];

        Assert.Equal(HttpStatusCode.Forbidden,
            (await cliente.GetAsync($"/api/videos/{video.Id}/renditions/360p.m3u8?t={token}")).StatusCode);
    }
}
