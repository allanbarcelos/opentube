using System.Net;
using OpenTube.Domain.Enums;
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

    private static async Task<HttpResponseMessage> PerguntarAsync(HttpClient cliente, string caminho)
    {
        using var pedido = new HttpRequestMessage(HttpMethod.Get, "/_authz");
        pedido.Headers.Add("X-Forwarded-Uri", caminho);

        return await cliente.SendAsync(pedido);
    }

    [Fact]
    public async Task A_playlist_aponta_para_o_caminho_servido_pelo_proxy()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var playlist = await cliente.GetStringAsync($"/api/videos/{video.Id}/versoes/360p.m3u8");

        Assert.Contains($"/vod/{video.Id}/360p/seg-00000.m4s", playlist);
        // Sem assinatura: quem autoriza agora é a própria aplicação, a cada pedido.
        Assert.DoesNotContain("X-Amz-Signature", playlist);
    }

    [Fact]
    public async Task O_segmento_de_video_publico_e_autorizado()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var resposta = await PerguntarAsync(cliente, $"/vod/{video.Id}/360p/seg-00000.m4s");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task O_segmento_de_video_sem_acesso_e_recusado()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Private);

        using var cliente = _app.CreateBrowser();
        var resposta = await PerguntarAsync(cliente, $"/vod/{video.Id}/360p/seg-00000.m4s");

        Assert.NotEqual(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task O_administrador_alcanca_o_segmento_do_video_privado()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Private);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        Assert.Equal(HttpStatusCode.OK,
            (await PerguntarAsync(cliente, $"/vod/{video.Id}/360p/seg-00000.m4s")).StatusCode);
    }

    [Fact]
    public async Task Revogar_o_acesso_barra_o_proximo_segmento()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        Assert.Equal(HttpStatusCode.OK, (await PerguntarAsync(cliente, $"/vod/{video.Id}/360p/seg-1.m4s")).StatusCode);

        await using (var db = postgres.CreateContext())
        {
            var alvo = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .SingleAsync(db.Videos, v => v.Id == video.Id);
            alvo.ChangeVisibility(VideoVisibility.Private);
            await db.SaveChangesAsync();
        }

        // É a diferença em relação ao endereço assinado: o corte vale no segmento seguinte.
        Assert.NotEqual(HttpStatusCode.OK, (await PerguntarAsync(cliente, $"/vod/{video.Id}/360p/seg-2.m4s")).StatusCode);
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
