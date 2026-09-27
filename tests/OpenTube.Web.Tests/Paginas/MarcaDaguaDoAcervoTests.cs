// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Paginas;

/// <summary>
/// Marca d'água do acervo: imagem PNG definida pela administração, na posição escolhida,
/// sobre todos os vídeos.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class MarcaDaguaDoAcervoTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
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

    /// <summary>Envia o formulário da página como o navegador: multipart, com o token.</summary>
    private static async Task<HttpResponseMessage> EnviarAsync(HttpClient cliente, byte[]? arquivo, WatermarkPosition posicao)
    {
        var token = await FormularioHelpers.TokenAntifalsificacaoAsync(cliente, "/admin/watermark");

        using var conteudo = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent(((int)posicao).ToString()), "posicao" }
        };

        // Sem arquivo, a parte não vai: é o caso "mudar só a posição".
        if (arquivo is not null)
        {
            var bytes = new ByteArrayContent(arquivo);
            bytes.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            conteudo.Add(bytes, "arquivo", "logo.png");
        }

        return await cliente.PostAsync("/admin/watermark/save", conteudo);
    }

    /// <summary>Endereço da imagem da marca na página, com a versão.</summary>
    private static string? EnderecoDaMarca(string html) =>
        Regex.Match(html, "src=\"(/branding/watermark\\.png\\?v=\\d+)\"") is { Success: true } achado
            ? achado.Groups[1].Value
            : null;

    [Fact]
    public async Task Videos_que_ja_estavam_na_plataforma_recebem_a_marca_e_as_trocas()
    {
        using var storage = minio.CreateStorage();

        // Publicado antes de existir qualquer marca: nada nele foi gerado com ela.
        var antigo = await AcervoDeTeste.PublicarAsync(postgres, storage, "Vídeo antigo", VideoVisibility.Public);

        using var admin = _app.CreateBrowser();
        await EntrarComoAdminAsync(admin);
        await EnviarAsync(admin, PngDeTeste.Criar(400, 200), WatermarkPosition.BottomRight);

        var novo = await AcervoDeTeste.PublicarAsync(postgres, storage, "Vídeo novo", VideoVisibility.Public);

        using var visitante = _app.CreateBrowser();
        var primeiraAntigo = EnderecoDaMarca(await visitante.GetStringAsync($"/watch/{antigo.Slug}"));
        var primeiraNovo = EnderecoDaMarca(await visitante.GetStringAsync($"/watch/{novo.Slug}"));

        Assert.NotNull(primeiraAntigo);
        Assert.Equal(primeiraAntigo, primeiraNovo);

        // Troca a imagem e a posição: vale na hora para os dois, sem reprocessar vídeo algum.
        await Task.Delay(5);
        await EnviarAsync(admin, PngDeTeste.Criar(300, 300, (_, _) => (10, 120, 250, 255)), WatermarkPosition.TopLeft);

        var htmlAntigo = await visitante.GetStringAsync($"/watch/{antigo.Slug}");
        var htmlNovo = await visitante.GetStringAsync($"/watch/{novo.Slug}");
        var segundaAntigo = EnderecoDaMarca(htmlAntigo);

        Assert.NotEqual(primeiraAntigo, segundaAntigo);
        Assert.Equal(segundaAntigo, EnderecoDaMarca(htmlNovo));
        Assert.Contains("marca-logo marca-logo-top-left", htmlAntigo);
        Assert.Contains("marca-logo marca-logo-top-left", htmlNovo);

        var atual = await visitante.GetByteArrayAsync(segundaAntigo);
        Assert.Equal((300, 300), PlayerWatermark.ReadPngSize(atual));

        // Quem ainda tiver o endereço antigo não fica com a imagem velha em cache: a resposta
        // já é a nova e manda conferir de novo.
        var velho = await visitante.GetAsync(primeiraAntigo);
        Assert.True(velho.Headers.CacheControl!.NoCache);
        Assert.Equal(atual, await velho.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_marca_ocupa_a_area_padrao_qualquer_que_seja_a_imagem()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var admin = _app.CreateBrowser();
        await EntrarComoAdminAsync(admin);

        // Faixa larga, quadrado e retrato: a área na página é sempre a mesma; a imagem se
        // ajusta a ela sem distorcer.
        foreach (var (largura, altura) in new[] { (1600, 200), (500, 500), (200, 600) })
        {
            await EnviarAsync(admin, PngDeTeste.Criar(largura, altura), WatermarkPosition.BottomRight);

            var html = await admin.GetStringAsync($"/watch/{video.Slug}");

            Assert.Contains("style=\"width:10%;height:12%;opacity:0.75;bottom:12%;right:5%\"", html);
        }
    }

    [Fact]
    public async Task A_marca_definida_aparece_sobre_os_videos_na_posicao_escolhida()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var admin = _app.CreateBrowser();
        await EntrarComoAdminAsync(admin);

        var resposta = await EnviarAsync(admin, PngDeTeste.Criar(400, 200), WatermarkPosition.TopLeft);
        Assert.Equal("/admin/watermark?salvo=1", resposta.Headers.Location!.ToString());

        // A página para onde o envio leva precisa abrir: é onde a administração vê o resultado.
        var confirmacao = await admin.GetAsync(resposta.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, confirmacao.StatusCode);
        Assert.Contains("Watermark saved.", await confirmacao.Content.ReadAsStringAsync());

        // Quem assiste não precisa estar identificado: a marca do acervo vale para todos.
        using var visitante = _app.CreateBrowser();
        var html = await visitante.GetStringAsync($"/watch/{video.Slug}");

        Assert.Contains("class=\"marca-logo marca-logo-top-left\"", html);
        Assert.Contains("src=\"/branding/watermark.png?v=", html);
        Assert.Contains("data-logo-canto=\"0\"", html);
    }

    [Fact]
    public async Task A_imagem_e_servida_intacta_e_com_cache_so_na_versao_atual()
    {
        var png = PngDeTeste.Criar(400, 200);

        using var admin = _app.CreateBrowser();
        await EntrarComoAdminAsync(admin);
        await EnviarAsync(admin, png, WatermarkPosition.BottomRight);

        var pagina = await admin.GetStringAsync("/admin/watermark");
        var endereco = pagina.Split("src=\"")[1..].First(s => s.StartsWith("/branding/")).Split('"')[0];

        using var visitante = _app.CreateBrowser();
        var comVersao = await visitante.GetAsync(endereco);
        var semVersao = await visitante.GetAsync("/branding/watermark.png");

        Assert.Equal(HttpStatusCode.OK, comVersao.StatusCode);
        Assert.Equal("image/png", comVersao.Content.Headers.ContentType!.MediaType);
        await using (var db = postgres.CreateContext())
            Assert.Equal((await db.PlayerWatermarks.SingleAsync()).Image, await comVersao.Content.ReadAsByteArrayAsync());
        Assert.Contains("immutable", comVersao.Headers.CacheControl!.ToString());
        Assert.True(semVersao.Headers.CacheControl!.NoCache);
    }

    [Fact]
    public async Task Sem_arquivo_muda_so_a_posicao()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var admin = _app.CreateBrowser();
        await EntrarComoAdminAsync(admin);
        await EnviarAsync(admin, PngDeTeste.Criar(400, 200), WatermarkPosition.TopLeft);
        await EnviarAsync(admin, null, WatermarkPosition.Center);

        var html = await admin.GetStringAsync($"/watch/{video.Slug}");

        Assert.Contains("marca-logo-center", html);

        await using var db = postgres.CreateContext();
        Assert.Equal(400, (await db.PlayerWatermarks.SingleAsync()).Width);
    }

    [Fact]
    public async Task Arquivo_que_nao_e_png_e_recusado_com_mensagem()
    {
        using var admin = _app.CreateBrowser();
        await EntrarComoAdminAsync(admin);

        var resposta = await EnviarAsync(admin, "GIF89a não é PNG"u8.ToArray(), WatermarkPosition.TopLeft);

        Assert.StartsWith("/admin/watermark?erro=", resposta.Headers.Location!.ToString());
        Assert.Contains("The file is not a PNG image.", await admin.GetStringAsync(resposta.Headers.Location!.ToString()));

        await using var db = postgres.CreateContext();
        Assert.False(await db.PlayerWatermarks.AnyAsync());
    }

    [Fact]
    public async Task Quem_nao_e_administrador_nao_define_a_marca()
    {
        using var visitante = _app.CreateBrowser();

        using var conteudo = new MultipartFormDataContent
        {
            { new StringContent("0"), "posicao" },
            { new ByteArrayContent(PngDeTeste.Criar()), "arquivo", "logo.png" }
        };
        var resposta = await visitante.PostAsync("/admin/watermark/save", conteudo);

        Assert.NotEqual(HttpStatusCode.OK, resposta.StatusCode);
        Assert.NotEqual("/admin/watermark?salvo=1", resposta.Headers.Location?.ToString());

        await using var db = postgres.CreateContext();
        Assert.False(await db.PlayerWatermarks.AnyAsync());
    }

    [Fact]
    public async Task Remover_tira_a_marca_dos_videos()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Boas-vindas", VideoVisibility.Public);

        using var admin = _app.CreateBrowser();
        await EntrarComoAdminAsync(admin);
        await EnviarAsync(admin, PngDeTeste.Criar(), WatermarkPosition.TopLeft);

        var remocao = await FormularioHelpers.EnviarFormularioAsync(admin, "/admin/watermark", "/admin/watermark/remove", new Dictionary<string, string>());
        var confirmacao = await admin.GetAsync(remocao.Headers.Location!.ToString());

        Assert.Equal(HttpStatusCode.OK, confirmacao.StatusCode);
        Assert.Contains("Watermark removed.", await confirmacao.Content.ReadAsStringAsync());
        Assert.DoesNotContain("marca-logo", await admin.GetStringAsync($"/watch/{video.Slug}"));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/branding/watermark.png")).StatusCode);
    }

    [Fact]
    public async Task As_acoes_ficam_na_auditoria()
    {
        using var admin = _app.CreateBrowser();
        await EntrarComoAdminAsync(admin);
        await EnviarAsync(admin, PngDeTeste.Criar(), WatermarkPosition.TopLeft);
        await EnviarAsync(admin, null, WatermarkPosition.BottomRight);
        await FormularioHelpers.EnviarFormularioAsync(admin, "/admin/watermark", "/admin/watermark/remove", new Dictionary<string, string>());

        await using var db = postgres.CreateContext();
        var acoes = await db.AuditEntries.OrderBy(a => a.At).Select(a => a.Action).ToListAsync();

        Assert.Equal(["marca.definida", "marca.reposicionada", "marca.removida"], acoes);
    }
}
