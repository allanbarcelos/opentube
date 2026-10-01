// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using OpenTube.Domain.Access;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Storage;
using OpenTube.TestSupport;
using OpenTube.Web.Endpoints;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Endpoints;

/// <summary>Leitura do caminho do segmento, que não depende de servidor.</summary>
public class CaminhoDoSegmentoTests
{
    private static readonly Guid Video = Guid.Parse("0199a0b0-0000-7000-8000-000000000001");

    [Theory]
    [InlineData("/vod/0199a0b0-0000-7000-8000-000000000001/720p/seg-00001.m4s")]
    [InlineData("/vod/0199a0b0-0000-7000-8000-000000000001/master.m3u8")]
    [InlineData("/vod/0199a0b0-0000-7000-8000-000000000001/720p/init-720p.mp4?x=1")]
    public void Extrai_o_video_do_caminho(string caminho)
    {
        Assert.Equal(Video, SegmentAuthorizationEndpoints.ExtrairVideo(caminho, "/vod"));
    }

    [Theory]
    [InlineData("/outro/0199a0b0-0000-7000-8000-000000000001/seg.m4s")]
    [InlineData("/vod/nao-e-identificador/seg.m4s")]
    [InlineData("/vod/0199a0b0-0000-7000-8000-000000000001")]
    [InlineData("/vod/")]
    [InlineData("/vod/0199a0b0-0000-7000-8000-000000000001/../0199a0b0-0000-7000-8000-000000000002/720p/seg.m4s")]
    [InlineData("/vod/0199a0b0-0000-7000-8000-000000000001/%2e%2e/0199a0b0-0000-7000-8000-000000000002/seg.m4s")]
    [InlineData("/vod/0199a0b0-0000-7000-8000-000000000001//seg.m4s")]
    [InlineData("/vod/0199a0b0-0000-7000-8000-000000000001/./seg.m4s")]
    [InlineData("/vod/0199a0b0-0000-7000-8000-000000000001/..\\0199a0b0-0000-7000-8000-000000000002/seg.m4s")]
    [InlineData("")]
    [InlineData(null)]
    public void Caminho_fora_do_formato_nao_autoriza(string? caminho)
    {
        // Devolver nulo faz a autorização recusar, em vez de adivinhar.
        Assert.Null(SegmentAuthorizationEndpoints.ExtrairVideo(caminho, "/vod"));
    }

    [Fact]
    public void O_prefixo_e_respeitado()
    {
        Assert.Equal(Video, SegmentAuthorizationEndpoints.ExtrairVideo(
            "/midia/0199a0b0-0000-7000-8000-000000000001/seg.m4s", "midia"));
    }

    [Fact]
    public void Extrai_a_capa_da_colecao()
    {
        var capa = SegmentAuthorizationEndpoints.ExtrairMiniaturaDeColecao(
            "/vod/collections/0199a0b00000700080000000000000aa/thumb-10.jpg?v=1", "/vod");

        Assert.Equal(Guid.Parse("0199a0b0-0000-7000-8000-0000000000aa"), capa?.CollectionId);
        Assert.Equal("collections/0199a0b00000700080000000000000aa/thumb-10.jpg", capa?.Key);
    }

    [Theory]
    [InlineData("/vod/collections/0199a0b0-0000-7000-8000-0000000000aa/thumb-10.jpg")]
    [InlineData("/vod/collections/0199a0b00000700080000000000000aa/thumb-0.jpg")]
    [InlineData("/vod/collections/0199a0b00000700080000000000000aa/thumb-01.jpg")]
    [InlineData("/vod/collections/0199a0b00000700080000000000000aa/thumb-10.png")]
    [InlineData("/vod/collections/0199a0b00000700080000000000000aa/thumb-10.jpg/extra")]
    [InlineData("/vod/collections/0199a0b00000700080000000000000aa/../thumb-10.jpg")]
    [InlineData("/vod/collections/%2e%2e/0199a0b00000700080000000000000aa/thumb-10.jpg")]
    [InlineData("/vod/0199a0b0-0000-7000-8000-000000000001/thumb.jpg")]
    public void Capa_fora_do_formato_nao_autoriza(string caminho)
    {
        Assert.Null(SegmentAuthorizationEndpoints.ExtrairMiniaturaDeColecao(caminho, "/vod"));
    }
}

/// <summary>
/// Autorização de segmento com a aplicação inteira no ar, no modo em que o servidor da frente
/// entrega os arquivos.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AutorizacaoDeSegmentoTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private const string Admin = "allan@barcelos.dev";

    private OpenTubeWebFactory _app = default!;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();

        _app = new OpenTubeWebFactory(postgres, minio, Admin);
        _app.ComAutorizacaoDeSegmento = true;

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

    private static async Task<HttpResponseMessage> PerguntarAsync(
        HttpClient cliente, string caminho, IDictionary<string, string>? cabecalhos = null)
    {
        using var pedido = new HttpRequestMessage(HttpMethod.Get, "/_authz");
        pedido.Headers.Add("X-Forwarded-Uri", caminho);

        foreach (var (nome, valor) in cabecalhos ?? new Dictionary<string, string>())
            pedido.Headers.Add(nome, valor);

        return await cliente.SendAsync(pedido);
    }

    /// <summary>Token da reprodução que a playlist da versão pôs nos segmentos, para quem a pediu.</summary>
    private static async Task<string> TokenDaReproducaoAsync(HttpClient cliente, string slug)
    {
        var versao = await cliente.GetStringAsync(await Reproducao.VersaoPelaPaginaAsync(cliente, slug, "360p"));
        var segmento = Reproducao.PrimeiroSegmento(versao);

        return segmento[(segmento.IndexOf("?t=", StringComparison.Ordinal) + 3)..];
    }

    [Fact]
    public async Task A_playlist_aponta_para_o_caminho_servido_pelo_proxy()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var playlist = await cliente.GetStringAsync(await Reproducao.VersaoPelaPaginaAsync(cliente, video.Slug, "360p"));

        // O segmento leva o token da reprodução, que o servidor da frente repassa à aplicação.
        Assert.Contains($"/vod/{video.Id}/360p/seg-00000.m4s?t=", playlist);
        // Sem assinatura: quem autoriza agora é a própria aplicação, a cada pedido.
        Assert.DoesNotContain("X-Amz-Signature", playlist);
    }

    [Fact]
    public async Task O_segmento_de_video_publico_e_autorizado()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var token = await TokenDaReproducaoAsync(cliente, video.Slug);
        var resposta = await PerguntarAsync(cliente, $"/vod/{video.Id}/360p/seg-00000.m4s?t={token}");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task O_segmento_de_video_sem_acesso_e_recusado()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Private);

        using var cliente = _app.CreateBrowser();
        // Um token legítimo não basta: o acesso continua sendo conferido.
        var token = Uri.EscapeDataString(_app.Services.GetRequiredService<OpenTube.Infrastructure.Playback.PlaybackTokens>()
            .Issue(OpenTube.Infrastructure.Playback.PlaybackTokenKind.Playback, video.Id, Viewer.Anonymous));
        var resposta = await PerguntarAsync(cliente, $"/vod/{video.Id}/360p/seg-00000.m4s?t={token}");

        Assert.NotEqual(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task O_administrador_alcanca_o_segmento_do_video_privado()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Private);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);
        var token = await TokenDaReproducaoAsync(cliente, video.Slug);

        Assert.Equal(HttpStatusCode.OK,
            (await PerguntarAsync(cliente, $"/vod/{video.Id}/360p/seg-00000.m4s?t={token}")).StatusCode);
    }

    [Fact]
    public async Task Revogar_o_acesso_barra_o_proximo_segmento()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var token = await TokenDaReproducaoAsync(cliente, video.Slug);
        Assert.Equal(HttpStatusCode.OK, (await PerguntarAsync(cliente, $"/vod/{video.Id}/360p/seg-1.m4s?t={token}")).StatusCode);

        await using (var db = postgres.CreateContext())
        {
            var alvo = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .SingleAsync(db.Videos, v => v.Id == video.Id);
            alvo.ChangeVisibility(VideoVisibility.Private);
            await db.SaveChangesAsync();
        }

        // É a diferença em relação ao endereço assinado: o corte vale no segmento seguinte.
        Assert.NotEqual(HttpStatusCode.OK, (await PerguntarAsync(cliente, $"/vod/{video.Id}/360p/seg-2.m4s?t={token}")).StatusCode);
    }

    [Fact]
    public async Task O_segmento_sem_o_token_da_reproducao_e_recusado()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var token = await TokenDaReproducaoAsync(cliente, video.Slug);

        Assert.Equal(HttpStatusCode.Forbidden, (await PerguntarAsync(cliente, $"/vod/{video.Id}/360p/seg-00000.m4s")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PerguntarAsync(cliente, $"/vod/{video.Id}/360p/init-360p.mp4?t=1.x")).StatusCode);

        // Legendas e miniaturas são pedidas pela própria página, sem token.
        Assert.Equal(HttpStatusCode.OK, (await PerguntarAsync(cliente, $"/vod/{video.Id}/thumbnail.jpg")).StatusCode);

        // O mesmo token, num arquivo de outro vídeo, não vale.
        var outro = await AcervoDeTeste.PublicarAsync(postgres, storage, "Outro", VideoVisibility.Public);
        Assert.Equal(HttpStatusCode.Forbidden, (await PerguntarAsync(cliente, $"/vod/{outro.Id}/360p/seg-00000.m4s?t={token}")).StatusCode);
    }

    [Fact]
    public async Task Abrir_o_segmento_direto_numa_aba_e_recusado()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var token = await TokenDaReproducaoAsync(cliente, video.Slug);
        var caminho = $"/vod/{video.Id}/360p/seg-00000.m4s?t={token}";

        var navegacao = new Dictionary<string, string> { ["Sec-Fetch-Mode"] = "navigate", ["Sec-Fetch-Dest"] = "document" };
        Assert.Equal(HttpStatusCode.Forbidden, (await PerguntarAsync(cliente, caminho, navegacao)).StatusCode);

        var player = new Dictionary<string, string>
        {
            ["Sec-Fetch-Mode"] = "cors", ["Sec-Fetch-Dest"] = "empty", ["Sec-Fetch-Site"] = "same-origin"
        };
        Assert.Equal(HttpStatusCode.OK, (await PerguntarAsync(cliente, caminho, player)).StatusCode);
    }

    [Fact]
    public async Task Segmentos_pedidos_rapido_demais_sao_barrados()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var token = await TokenDaReproducaoAsync(cliente, video.Slug);

        // A folga inicial (45 segmentos, três minutos de vídeo) passa de uma vez; o que vem
        // depois, pedido no mesmo instante, é o padrão de quem baixa o vídeo.
        for (var i = 0; i < 45; i++)
            Assert.Equal(HttpStatusCode.OK, (await PerguntarAsync(cliente, $"/vod/{video.Id}/360p/seg-{i:00000}.m4s?t={token}")).StatusCode);

        var excesso = await PerguntarAsync(cliente, $"/vod/{video.Id}/360p/seg-00045.m4s?t={token}");
        Assert.Equal(HttpStatusCode.TooManyRequests, excesso.StatusCode);
    }

    [Fact]
    public async Task A_capa_da_colecao_visivel_e_autorizada_e_a_outra_nao()
    {
        using var storage = minio.CreateStorage();
        var aberta = await AcervoDeTeste.PublicarAsync(postgres, storage, "Aberta", VideoVisibility.Public);
        var fechada = await AcervoDeTeste.PublicarAsync(postgres, storage, "Fechada", VideoVisibility.Private);

        string chaveAberta, chaveFechada;
        Guid colecaoAberta;
        await using (var db = postgres.CreateContext())
        {
            var agora = DateTimeOffset.UtcNow;
            var autor = Guid.CreateVersion7();

            var publica = Collection.Create("Aberta", "aberta", autor, agora);
            publica.Add(aberta.Id, agora);
            chaveAberta = StorageKeys.CollectionThumbnail(publica.Id, 10);
            publica.SetThumbnail(chaveAberta, 10);

            var privada = Collection.Create("Fechada", "fechada", autor, agora);
            privada.Add(fechada.Id, agora);
            chaveFechada = StorageKeys.CollectionThumbnail(privada.Id, 10);
            privada.SetThumbnail(chaveFechada, 10);

            db.Collections.AddRange(publica, privada);
            await db.SaveChangesAsync();
            colecaoAberta = publica.Id;
        }

        using var cliente = _app.CreateBrowser();

        Assert.Equal(HttpStatusCode.OK, (await PerguntarAsync(cliente, "/vod/" + chaveAberta)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await PerguntarAsync(cliente, "/vod/" + chaveFechada)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK,
            (await PerguntarAsync(cliente, $"/vod/collections/{colecaoAberta:n}/thumb-11.jpg")).StatusCode);

        await EntrarComoAdminAsync(cliente);

        Assert.Equal(HttpStatusCode.OK, (await PerguntarAsync(cliente, "/vod/" + chaveFechada)).StatusCode);
    }

    [Fact]
    public async Task Caminho_fora_do_formato_e_recusado()
    {
        using var cliente = _app.CreateBrowser();

        Assert.NotEqual(HttpStatusCode.OK, (await PerguntarAsync(cliente, "/etc/passwd")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await PerguntarAsync(cliente, "/vod/qualquer-coisa")).StatusCode);
    }

    [Fact]
    public async Task Sem_o_recurso_ligado_o_endereco_nao_existe()
    {
        await using var outro = new OpenTubeWebFactory(postgres, minio, Admin);
        using var cliente = outro.CreateBrowser();

        Assert.Equal(HttpStatusCode.NotFound, (await PerguntarAsync(cliente, "/vod/x/y.m4s")).StatusCode);
    }
}
