// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Entities;
using OpenTube.Infrastructure.Security;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Paginas;

/// <summary>
/// Personalização do site pela administração: o nome na barra, nos títulos e nos emails, o
/// logotipo da barra e o que o rodapé mostra.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PersonalizacaoTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private const string Admin = "allan@barcelos.dev";
    private const string Pagina = "/admin/customization";

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
    private static async Task<HttpResponseMessage> SalvarAsync(
        HttpClient cliente, string nome, bool credito = true, bool repositorio = true, byte[]? logo = null)
    {
        var token = await FormularioHelpers.TokenAntifalsificacaoAsync(cliente, Pagina);

        using var conteudo = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent(nome), "nome" }
        };

        // Caixa desmarcada não vai no formulário, como no navegador.
        if (credito)
            conteudo.Add(new StringContent("true"), "credito");
        if (repositorio)
            conteudo.Add(new StringContent("true"), "repositorio");

        if (logo is not null)
        {
            var bytes = new ByteArrayContent(logo);
            bytes.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            conteudo.Add(bytes, "logo", "logo.png");
        }

        return await cliente.PostAsync(Pagina + "/save", conteudo);
    }

    private static string? EnderecoDoLogo(string html) =>
        Regex.Match(html, "src=\"(/branding/logo\\.png\\?v=\\d+)\"") is { Success: true } achado
            ? achado.Groups[1].Value
            : null;

    private static string Titulo(string html) => Regex.Match(html, "<title>([^<]*)</title>").Groups[1].Value;

    [Fact]
    public async Task Sem_personalizacao_o_site_se_apresenta_como_OpenTube()
    {
        using var visitante = _app.CreateBrowser();
        var html = await visitante.GetStringAsync("/sign-in");

        Assert.Equal("Sign in — OpenTube", Titulo(html));
        Assert.Contains("<span>OpenTube</span>", html);
        Assert.Contains("data-credito=\"autor\"", html);
        Assert.Contains("data-credito=\"opentube\"", html);
        Assert.Contains("name=\"generator\"", html);
        Assert.Null(EnderecoDoLogo(html));
    }

    [Fact]
    public async Task O_nome_definido_aparece_na_barra_e_nos_titulos_e_o_rodape_obedece()
    {
        using var admin = _app.CreateBrowser();
        await EntrarComoAdminAsync(admin);

        var resposta = await SalvarAsync(admin, "Vídeos da Acme", credito: false, repositorio: false);
        Assert.Equal($"{Pagina}?salvo=1", resposta.Headers.Location!.ToString());

        using var visitante = _app.CreateBrowser();
        var entrada = await visitante.GetStringAsync("/sign-in");
        var inicio = await visitante.GetStringAsync("/");

        Assert.Equal("Sign in — Vídeos da Acme", Titulo(entrada));
        Assert.Equal("Vídeos da Acme", Titulo(inicio));
        Assert.Contains("<span>Vídeos da Acme</span>", inicio);
        Assert.DoesNotContain("<span>OpenTube</span>", inicio);

        // Crédito, link do repositório e a meta que leva a ele somem juntos.
        Assert.DoesNotContain("Barcelos.Dev", inicio);
        Assert.DoesNotContain("github.com/allanbarcelos/opentube", inicio);

        // E voltam quando a administração os marca de novo.
        await SalvarAsync(admin, "Vídeos da Acme", credito: true, repositorio: true);
        var depois = await visitante.GetStringAsync("/");

        Assert.Contains("data-credito=\"autor\"", depois);
        Assert.Contains("data-credito=\"opentube\"", depois);
    }

    [Fact]
    public async Task O_codigo_de_acesso_chega_com_o_nome_do_site()
    {
        using var admin = _app.CreateBrowser();
        await EntrarComoAdminAsync(admin);
        await SalvarAsync(admin, "Acme TV");

        _app.Emails.Clear();
        using var outro = _app.CreateBrowser();
        await FormularioHelpers.EnviarFormularioAsync(
            outro, "/sign-in", "/sign-in/code", new Dictionary<string, string> { ["email"] = Admin });

        var mensagem = Assert.Single(_app.Emails.Sent);
        Assert.Contains("Acme TV", mensagem.Subject);
        Assert.Contains(">Acme TV</h1>", mensagem.HtmlBody);
        Assert.DoesNotContain("OpenTube", mensagem.Subject + mensagem.HtmlBody);
    }

    [Fact]
    public async Task O_logotipo_e_reduzido_aparece_na_barra_e_pode_ser_removido()
    {
        using var admin = _app.CreateBrowser();
        await EntrarComoAdminAsync(admin);

        // O dobro do tamanho padrão: chega reduzido à metade, sem distorcer.
        await SalvarAsync(admin, "Acme", logo: PngDeTeste.Criar(SiteBranding.LogoStandardWidth * 2, SiteBranding.LogoStandardHeight * 2));

        using var visitante = _app.CreateBrowser();
        var html = await visitante.GetStringAsync("/");
        var endereco = EnderecoDoLogo(html);

        Assert.NotNull(endereco);
        Assert.Contains("width=\"120\"", html);

        var logo = await visitante.GetAsync(endereco);
        Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
        Assert.Equal("image/png", logo.Content.Headers.ContentType!.MediaType);
        Assert.Equal(
            (SiteBranding.LogoStandardWidth, SiteBranding.LogoStandardHeight),
            PlayerWatermark.ReadPngSize(await logo.Content.ReadAsByteArrayAsync()));

        // Salvar sem arquivo mantém o logotipo.
        await SalvarAsync(admin, "Acme Vídeos");
        Assert.NotNull(EnderecoDoLogo(await visitante.GetStringAsync("/")));

        var token = await FormularioHelpers.TokenAntifalsificacaoAsync(admin, Pagina);
        await admin.PostAsync(Pagina + "/remove-logo",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        Assert.Null(EnderecoDoLogo(await visitante.GetStringAsync("/")));
        Assert.Equal(HttpStatusCode.NotFound, (await visitante.GetAsync("/branding/logo.png")).StatusCode);
    }

    [Fact]
    public async Task Nome_invalido_ou_imagem_que_nao_e_png_nao_mudam_nada()
    {
        using var admin = _app.CreateBrowser();
        await EntrarComoAdminAsync(admin);

        var vazio = await SalvarAsync(admin, "   ");
        var gif = await SalvarAsync(admin, "Acme", logo: "GIF89a nada de png"u8.ToArray());

        Assert.Contains("erro=", vazio.Headers.Location!.ToString());
        Assert.Contains("erro=", gif.Headers.Location!.ToString());

        // A imagem é conferida antes de gravar: o nome que veio junto também não ficou.
        await using var db = postgres.CreateContext();
        Assert.Empty(await db.SiteBrandings.ToListAsync());
    }

    [Fact]
    public async Task Cada_alteracao_fica_no_registro_de_auditoria()
    {
        using var admin = _app.CreateBrowser();
        await EntrarComoAdminAsync(admin);

        await SalvarAsync(admin, "Acme", logo: PngDeTeste.Criar(200, 100));

        var token = await FormularioHelpers.TokenAntifalsificacaoAsync(admin, Pagina);
        await admin.PostAsync(Pagina + "/remove-logo",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        await using var db = postgres.CreateContext();
        var registros = await db.AuditEntries
            .Where(e => e.EntityType == AuditEntities.Personalizacao)
            .Select(e => e.Action)
            .ToListAsync();

        Assert.Contains(AuditActions.PersonalizacaoAlterada, registros);
        Assert.Contains(AuditActions.LogoRemovido, registros);
    }

    [Fact]
    public async Task So_a_administracao_abre_a_personalizacao()
    {
        using var visitante = _app.CreateBrowser();

        var resposta = await visitante.GetAsync(Pagina);

        Assert.NotEqual(HttpStatusCode.OK, resposta.StatusCode);
        await using var db = postgres.CreateContext();
        Assert.Empty(await db.SiteBrandings.ToListAsync());
    }
}
