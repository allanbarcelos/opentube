using OpenTube.Domain.Enums;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Paginas;

/// <summary>
/// Marca d'água sobre o vídeo. Não impede gravação de tela, mas identifica a origem de um
/// vazamento e inibe o repasse casual.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class MarcaDaguaTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private const string Admin = "allan@barcelos.dev";

    private OpenTubeWebFactory _app = default!;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        _app = new OpenTubeWebFactory(postgres, minio, Admin);

        using var cliente = _app.CreateBrowser();
        await cliente.GetAsync("/saude");
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task EntrarAsync(HttpClient cliente, string email)
    {
        _app.Emails.Clear();

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/entrar", "/entrar/codigo", new Dictionary<string, string> { ["email"] = email });

        await FormularioHelpers.EnviarFormularioAsync(
            cliente,
            $"/entrar?email={Uri.EscapeDataString(email)}&enviado=1",
            "/entrar/verificar",
            new Dictionary<string, string> { ["email"] = email, ["codigo"] = _app.Emails.LastCode() });
    }

    [Fact]
    public async Task Quem_entrou_ve_o_proprio_endereco_sobre_o_video()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        var html = await cliente.GetStringAsync($"/v/{video.Slug}");

        Assert.Contains("marca-dagua", html);
        Assert.Contains($">{Admin}</span>", html);
        Assert.Contains("js/marca-dagua.js", html);
    }

    [Fact]
    public async Task Visitante_anonimo_nao_recebe_marca_dagua()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var html = await cliente.GetStringAsync($"/v/{video.Slug}");

        // Sem identidade não há o que marcar; um rótulo genérico só atrapalharia a leitura.
        Assert.DoesNotContain("id=\"marca-dagua\"", html);
    }
}
