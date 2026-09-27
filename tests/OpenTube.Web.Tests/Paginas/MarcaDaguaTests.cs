// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.Extensions.DependencyInjection;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Access;
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

    [Fact]
    public async Task Quem_entrou_ve_o_proprio_endereco_sobre_o_video()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        var html = await cliente.GetStringAsync($"/watch/{video.Slug}");

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
        var html = await cliente.GetStringAsync($"/watch/{video.Slug}");

        // Sem identidade não há o que marcar; um rótulo genérico só atrapalharia a leitura.
        Assert.DoesNotContain("id=\"marca-dagua\"", html);
    }

    [Fact]
    public async Task Quem_entrou_pelo_link_secreto_ve_a_identificacao_do_link()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano", VideoVisibility.Restricted);

        using var escopo = _app.Services.CreateScope();
        var link = await escopo.ServiceProvider.GetRequiredService<GrantService>().CreateShareLinkAsync(
            GrantTargetType.Video, video.Id, GrantValidity.Forever, Guid.CreateVersion7());

        using var cliente = _app.CreateBrowser();
        await cliente.GetAsync(new Uri(link.Url).PathAndQuery);

        var html = await cliente.GetStringAsync($"/watch/{video.Slug}");

        // Sem email, a marca leva o começo do identificador da concessão: é o que liga uma
        // gravação vazada ao link que a originou.
        Assert.Contains($">link {link.GrantId:n}"[..14], html);
        Assert.Contains("id=\"marca-dagua-mosaico\"", html);
    }

    [Fact]
    public async Task O_player_sai_sem_download_e_com_tela_cheia_e_janela_avulsa_liberadas()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        var html = await cliente.GetStringAsync($"/watch/{video.Slug}");

        Assert.Contains("controlslist=\"nodownload nofullscreen noremoteplayback\"", html);
        Assert.Contains("disableremoteplayback", html);
        // Tela cheia e Picture-in-Picture são do usuário; a marca d'água os acompanha.
        Assert.DoesNotContain("disablepictureinpicture", html);
    }
}
