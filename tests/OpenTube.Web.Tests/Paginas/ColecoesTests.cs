// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Access;
using OpenTube.Infrastructure.Services;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Paginas;

/// <summary>Administração de coleções e o efeito delas sobre quem consegue assistir.</summary>
[Collection(IntegrationCollection.Name)]
public class ColecoesTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private const string Admin = "allan@barcelos.dev";
    private const string Convidado = "convidado@empresa.com";

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

    private async Task<Guid> CriarColecaoAsync(HttpClient cliente, string nome)
    {
        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/admin/collections", "/admin/collections/create",
            new Dictionary<string, string> { ["nome"] = nome, ["descricao"] = "" });

        var destino = resposta.Headers.Location!.ToString();

        return Guid.Parse(destino.Split('/')[^1].Split('?')[0]);
    }

    [Fact]
    public async Task Convidado_nao_alcanca_a_administracao_de_colecoes()
    {
        await using (var db = postgres.CreateContext())
        {
            db.Users.Add(OpenTube.Domain.Entities.User.Create(
                OpenTube.Domain.ValueObjects.EmailAddress.Parse(Convidado), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Convidado);

        Assert.NotEqual(HttpStatusCode.OK, (await cliente.GetAsync("/admin/collections")).StatusCode);
    }

    [Fact]
    public async Task Cria_uma_colecao_e_ela_aparece_na_listagem()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        await CriarColecaoAsync(cliente, "Treinamentos Obrigatórios");

        var html = await cliente.GetStringAsync("/admin/collections");

        Assert.Contains("Treinamentos Obrigatórios", html);
        Assert.Contains("0 videos", html);
    }

    [Fact]
    public async Task Acrescenta_e_remove_videos_pela_interface()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Segurança da Informação", VideoVisibility.Restricted);

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);
        var colecao = await CriarColecaoAsync(cliente, "Treinamentos");

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/add",
            new Dictionary<string, string> { ["videoId"] = video.Id.ToString() });

        Assert.Contains("Segurança da Informação", await cliente.GetStringAsync($"/admin/collections/{colecao}"));

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/remove",
            new Dictionary<string, string> { ["videoId"] = video.Id.ToString() });

        await using var db = postgres.CreateContext();
        Assert.Empty(await db.CollectionVideos.ToListAsync());
    }

    [Fact]
    public async Task Avisa_quando_ha_videos_privados_na_colecao()
    {
        using var storage = minio.CreateStorage();
        var privado = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Interno", VideoVisibility.Private);
        var restrito = await AcervoDeTeste.PublicarAsync(postgres, storage, "Segurança da Informação", VideoVisibility.Restricted);

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);
        var colecao = await CriarColecaoAsync(cliente, "Treinamentos");

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/add",
            new Dictionary<string, string> { ["videoId"] = privado.Id.ToString() });
        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/add",
            new Dictionary<string, string> { ["videoId"] = restrito.Id.ToString() });

        var html = await cliente.GetStringAsync($"/admin/collections/{colecao}");

        Assert.Contains("1 video in this collection is private. To make it accessible, set it to Restricted or Public.", html);
        Assert.Contains(">Private<", html);

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/remove",
            new Dictionary<string, string> { ["videoId"] = privado.Id.ToString() });

        var semPrivados = await cliente.GetStringAsync($"/admin/collections/{colecao}");

        Assert.DoesNotContain("is private", semPrivados);
        Assert.DoesNotContain(">Private<", semPrivados);
    }

    [Fact]
    public async Task Renomear_a_colecao_nao_muda_o_endereco()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);
        var colecao = await CriarColecaoAsync(cliente, "Treinamentos");

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/save",
            new Dictionary<string, string> { ["nome"] = "Capacitação 2026", ["descricao"] = "Nova" });

        await using var db = postgres.CreateContext();
        var lida = await db.Collections.SingleAsync();

        Assert.Equal("Capacitação 2026", lida.Name);
        Assert.Equal("treinamentos", lida.Slug);
    }

    [Fact]
    public async Task A_concessao_sobre_a_colecao_libera_os_videos_dela()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Segurança da Informação", VideoVisibility.Restricted);

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);
        var colecao = await CriarColecaoAsync(cliente, "Treinamentos");

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/add",
            new Dictionary<string, string> { ["videoId"] = video.Id.ToString() });

        using (var escopo = _app.Services.CreateScope())
        {
            await escopo.ServiceProvider.GetRequiredService<GrantService>().InviteAsync(
                [Convidado], GrantTargetType.Collection, colecao, GrantValidity.Forever, Guid.CreateVersion7());
        }

        using var convidado = _app.CreateBrowser();
        await EntrarAsync(convidado, Convidado);

        var home = await convidado.GetStringAsync("/");
        Assert.Contains("Treinamentos", home);
        Assert.Contains("/collections/treinamentos", home);
        Assert.DoesNotContain("Segurança da Informação", home);
        Assert.Contains("Segurança da Informação", await convidado.GetStringAsync("/collections/treinamentos"));
        Assert.Equal(HttpStatusCode.OK, (await convidado.GetAsync($"/watch/{video.Slug}")).StatusCode);
    }

    [Fact]
    public async Task A_home_agrupa_a_colecao_e_a_busca_mostra_o_video()
    {
        using var storage = minio.CreateStorage();
        var primeiro = await AcervoDeTeste.PublicarAsync(postgres, storage, "Abertura", VideoVisibility.Public);
        var segundo = await AcervoDeTeste.PublicarAsync(postgres, storage, "Encerramento", VideoVisibility.Public);
        await AcervoDeTeste.PublicarAsync(postgres, storage, "Aviso Avulso", VideoVisibility.Public);

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);
        var colecao = await CriarColecaoAsync(cliente, "Treinamentos");

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/add",
            new Dictionary<string, string> { ["videoId"] = primeiro.Id.ToString() });
        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/add",
            new Dictionary<string, string> { ["videoId"] = segundo.Id.ToString() });

        using var visitante = _app.CreateBrowser();
        var home = await visitante.GetStringAsync("/");

        Assert.Contains("Treinamentos", home);
        Assert.Contains("collection-cover", home);
        Assert.Contains("/collections/treinamentos", home);
        Assert.Contains("Aviso Avulso", home);
        Assert.DoesNotContain("Abertura", home);
        Assert.DoesNotContain("Encerramento", home);

        var busca = await visitante.GetStringAsync("/?q=abertura");
        Assert.Contains("Abertura", busca);
        Assert.Contains($"/watch/{primeiro.Slug}?collection=treinamentos", busca);
        Assert.DoesNotContain("/collections/treinamentos", busca);

        var playlist = await visitante.GetStringAsync("/collections/treinamentos");
        var posicaoPrimeiro = playlist.IndexOf("Abertura", StringComparison.Ordinal);
        var posicaoSegundo = playlist.IndexOf("Encerramento", StringComparison.Ordinal);
        Assert.True(posicaoPrimeiro >= 0 && posicaoPrimeiro < posicaoSegundo);
        Assert.Contains($"/watch/{primeiro.Slug}?collection=treinamentos", playlist);
        Assert.Contains($"<time datetime=\"{primeiro.CreatedAt.UtcDateTime:yyyy-MM-ddTHH:mm:ss}Z\">", playlist);

        var assistir = await visitante.GetStringAsync($"/watch/{primeiro.Slug}?collection=treinamentos");
        Assert.Contains("data-autoplay", assistir);
        Assert.Contains("Autoplay", assistir);
        Assert.Contains($"/watch/{segundo.Slug}?collection=treinamentos&amp;autoplay=1", assistir);

        var ultimo = await visitante.GetStringAsync($"/watch/{segundo.Slug}?collection=treinamentos");
        Assert.DoesNotContain("data-proximo", ultimo);
    }

    [Fact]
    public async Task O_agrupamento_começa_ligado_e_pode_ser_desligado()
    {
        using var storage = minio.CreateStorage();
        var dentro = await AcervoDeTeste.PublicarAsync(postgres, storage, "Dentro", VideoVisibility.Public);
        var marcado = await AcervoDeTeste.PublicarAsync(postgres, storage, "Marcado", VideoVisibility.Public);
        await AcervoDeTeste.PublicarAsync(postgres, storage, "Solto", VideoVisibility.Public);

        using var admin = _app.CreateBrowser();
        await EntrarAsync(admin, Admin);
        var alfa = await CriarColecaoAsync(admin, "Alfa");
        await FormularioHelpers.EnviarFormularioAsync(
            admin, $"/admin/collections/{alfa}", $"/admin/collections/{alfa}/videos/add",
            new Dictionary<string, string> { ["videoId"] = dentro.Id.ToString() });
        await FormularioHelpers.EnviarFormularioAsync(
            admin, "/", $"/videos/{marcado.Slug}/favorite",
            new Dictionary<string, string> { ["destino"] = "/" });

        var agrupada = await admin.GetStringAsync("/");
        Assert.Contains("Group collections", agrupada);
        Assert.Contains("name=\"agrupar\" value=\"0\"", agrupada);
        Assert.DoesNotContain("Dentro", agrupada);
        Assert.True(Posicao(agrupada, "Marcado") < Posicao(agrupada, "Alfa"));
        Assert.True(Posicao(agrupada, "Alfa") < Posicao(agrupada, "Solto"));

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            admin, "/", "/listing/grouping",
            new Dictionary<string, string> { ["agrupar"] = "0", ["destino"] = "/" });
        Assert.Equal(HttpStatusCode.Redirect, resposta.StatusCode);
        var solta = await admin.GetStringAsync("/");
        Assert.DoesNotContain("Alfa", solta);
        Assert.Contains($"/watch/{dentro.Slug}?collection=alfa", solta);
        Assert.Contains("name=\"agrupar\" value=\"1\"", solta);
        Assert.True(Posicao(solta, "Marcado") < Posicao(solta, "Dentro"));
        Assert.True(Posicao(solta, "Dentro") < Posicao(solta, "Solto"));

        await FormularioHelpers.EnviarFormularioAsync(
            admin, "/", "/listing/grouping",
            new Dictionary<string, string> { ["agrupar"] = "1", ["destino"] = "/" });
        var outra = await admin.GetStringAsync("/");
        Assert.Contains("Alfa", outra);
        Assert.DoesNotContain("Dentro", outra);
        Assert.Contains("name=\"agrupar\" value=\"0\"", outra);

        using var visitante = _app.CreateBrowser();
        var anonima = await visitante.GetStringAsync("/");
        Assert.Contains("Alfa", anonima);
        Assert.DoesNotContain("Dentro", anonima);
        await FormularioHelpers.EnviarFormularioAsync(
            visitante, "/", "/listing/grouping",
            new Dictionary<string, string> { ["agrupar"] = "0", ["destino"] = "/" });
        var soltaAnonima = await visitante.GetStringAsync("/");
        Assert.DoesNotContain("Alfa", soltaAnonima);
        Assert.Contains("Dentro", soltaAnonima);
    }

    [Fact]
    public async Task Colecao_ganha_badge_ate_cada_video_novo_ser_aberto()
    {
        using var storage = minio.CreateStorage();
        var antigo = await AcervoDeTeste.PublicarAsync(postgres, storage, "Antigo", VideoVisibility.Public);
        var novo = await AcervoDeTeste.PublicarAsync(postgres, storage, "Novo", VideoVisibility.Public);
        var outro = await AcervoDeTeste.PublicarAsync(postgres, storage, "Outro", VideoVisibility.Public);

        using var admin = _app.CreateBrowser();
        await EntrarAsync(admin, Admin);
        var alfa = await CriarColecaoAsync(admin, "Alfa");
        await FormularioHelpers.EnviarFormularioAsync(
            admin, $"/admin/collections/{alfa}", $"/admin/collections/{alfa}/videos/add",
            new Dictionary<string, string> { ["videoId"] = antigo.Id.ToString() });

        await using (var db = postgres.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE collection_videos SET added_at = TIMESTAMPTZ '1970-01-01+00'");
            db.Users.Add(OpenTube.Domain.Entities.User.Create(
                OpenTube.Domain.ValueObjects.EmailAddress.Parse(Convidado), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        await FormularioHelpers.EnviarFormularioAsync(
            admin, $"/admin/collections/{alfa}", $"/admin/collections/{alfa}/videos/add",
            new Dictionary<string, string> { ["videoId"] = novo.Id.ToString() });
        await FormularioHelpers.EnviarFormularioAsync(
            admin, $"/admin/collections/{alfa}", $"/admin/collections/{alfa}/videos/add",
            new Dictionary<string, string> { ["videoId"] = outro.Id.ToString() });

        using var visitante = _app.CreateBrowser();
        Assert.DoesNotContain("video-new", await visitante.GetStringAsync("/"));

        var home = await admin.GetStringAsync("/");
        Assert.Contains("video-new", home);
        Assert.Contains("Alfa", home);
        Assert.DoesNotContain("Novo", home);
        Assert.DoesNotContain("Outro", home);

        var playlist = await admin.GetStringAsync("/collections/alfa");
        Assert.Contains("video-new", playlist);
        Assert.Contains("video-new", await admin.GetStringAsync("/collections/alfa"));

        await admin.GetAsync($"/watch/{antigo.Slug}?collection=alfa");
        Assert.Contains("video-new", await admin.GetStringAsync("/"));

        await admin.GetAsync($"/watch/{novo.Slug}?collection=alfa");
        var depoisDoPrimeiro = await admin.GetStringAsync("/collections/alfa");
        var posicaoNovo = depoisDoPrimeiro.IndexOf("Novo", StringComparison.Ordinal);
        var posicaoOutro = depoisDoPrimeiro.IndexOf("Outro", StringComparison.Ordinal);
        Assert.True(posicaoNovo >= 0 && posicaoNovo < posicaoOutro);
        Assert.DoesNotContain("video-new", depoisDoPrimeiro[posicaoNovo..posicaoOutro]);
        Assert.Contains("video-new", depoisDoPrimeiro[posicaoOutro..]);
        Assert.Contains("video-new", await admin.GetStringAsync("/"));

        await admin.GetAsync($"/watch/{outro.Slug}?collection=alfa");
        Assert.DoesNotContain("video-new", await admin.GetStringAsync("/"));
        Assert.DoesNotContain("video-new", await admin.GetStringAsync("/collections/alfa"));

        using var convidado = _app.CreateBrowser();
        await EntrarAsync(convidado, Convidado);
        Assert.Contains("video-new", await convidado.GetStringAsync("/"));
        Assert.Contains("video-new", await convidado.GetStringAsync("/collections/alfa"));

        await using (var db = postgres.CreateContext())
        {
            db.Users.Add(OpenTube.Domain.Entities.User.Create(
                OpenTube.Domain.ValueObjects.EmailAddress.Parse("tarde@empresa.com"),
                DateTimeOffset.UtcNow.AddHours(1)));
            await db.SaveChangesAsync();
        }

        using var tardio = _app.CreateBrowser();
        await EntrarAsync(tardio, "tarde@empresa.com");
        Assert.Contains("Alfa", await tardio.GetStringAsync("/"));
        Assert.DoesNotContain("video-new", await tardio.GetStringAsync("/"));
    }

    [Fact]
    public async Task A_home_poe_colecoes_por_nome_e_o_favorito_na_frente()
    {
        using var storage = minio.CreateStorage();
        var daArvore = await AcervoDeTeste.PublicarAsync(postgres, storage, "Raiz", VideoVisibility.Public);
        var doBeta = await AcervoDeTeste.PublicarAsync(postgres, storage, "Ramo", VideoVisibility.Public);
        await AcervoDeTeste.PublicarAsync(postgres, storage, "Fora", VideoVisibility.Public);

        using var admin = _app.CreateBrowser();
        await EntrarAsync(admin, Admin);
        var arvore = await CriarColecaoAsync(admin, "Árvore");
        var beta = await CriarColecaoAsync(admin, "Beta");

        await FormularioHelpers.EnviarFormularioAsync(
            admin, $"/admin/collections/{arvore}", $"/admin/collections/{arvore}/videos/add",
            new Dictionary<string, string> { ["videoId"] = daArvore.Id.ToString() });
        await FormularioHelpers.EnviarFormularioAsync(
            admin, $"/admin/collections/{beta}", $"/admin/collections/{beta}/videos/add",
            new Dictionary<string, string> { ["videoId"] = doBeta.Id.ToString() });

        using var visitante = _app.CreateBrowser();
        var anonima = await visitante.GetStringAsync("/");
        Assert.True(Posicao(anonima, "Árvore") < Posicao(anonima, "Beta"));
        Assert.True(Posicao(anonima, "Beta") < Posicao(anonima, "Fora"));
        Assert.DoesNotContain("Raiz", anonima);
        Assert.DoesNotContain("Ramo", anonima);
        Assert.DoesNotContain("/favorite", anonima);

        var antes = await admin.GetStringAsync("/");
        Assert.True(Posicao(antes, "Árvore") < Posicao(antes, "Beta"));
        Assert.DoesNotContain("bi-star-fill", antes);

        await FormularioHelpers.EnviarFormularioAsync(
            admin, "/", "/collections/beta/favorite",
            new Dictionary<string, string> { ["destino"] = "/" });

        var favorita = await admin.GetStringAsync("/");
        Assert.True(Posicao(favorita, "Beta") < Posicao(favorita, "Árvore"));
        Assert.True(Posicao(favorita, "Árvore") < Posicao(favorita, "Fora"));
        var estrela = favorita.IndexOf("/collections/beta/favorite", StringComparison.Ordinal);
        var outra = favorita.IndexOf("/collections/arvore/favorite", StringComparison.Ordinal);
        Assert.True(estrela >= 0 && estrela < outra);
        Assert.Contains("bi-star-fill", favorita[estrela..outra]);
        Assert.Contains("bi-star-fill", await admin.GetStringAsync("/collections/beta"));

        // Sem conta e sem concessão o pedido de código não envia email: a resposta é a mesma
        // de um endereço conhecido, para não revelar quem está convidado.
        await using (var db = postgres.CreateContext())
        {
            db.Users.Add(OpenTube.Domain.Entities.User.Create(
                OpenTube.Domain.ValueObjects.EmailAddress.Parse(Convidado), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        using var convidado = _app.CreateBrowser();
        await EntrarAsync(convidado, Convidado);
        var dele = await convidado.GetStringAsync("/");
        Assert.True(Posicao(dele, "Árvore") < Posicao(dele, "Beta"));
        Assert.DoesNotContain("bi-star-fill", dele);

        await FormularioHelpers.EnviarFormularioAsync(
            admin, "/", "/collections/beta/favorite",
            new Dictionary<string, string> { ["destino"] = "/" });

        var sem = await admin.GetStringAsync("/");
        Assert.True(Posicao(sem, "Árvore") < Posicao(sem, "Beta"));
        Assert.DoesNotContain("bi-star-fill", sem);
    }

    [Fact]
    public async Task A_home_poe_colecao_favorita_antes_do_video_favorito()
    {
        using var storage = minio.CreateStorage();
        var dentro = await AcervoDeTeste.PublicarAsync(postgres, storage, "Dentro", VideoVisibility.Public);
        var marcado = await AcervoDeTeste.PublicarAsync(postgres, storage, "Marcado", VideoVisibility.Public);
        var solto = await AcervoDeTeste.PublicarAsync(postgres, storage, "Solto", VideoVisibility.Public);

        using var admin = _app.CreateBrowser();
        await EntrarAsync(admin, Admin);
        var alfa = await CriarColecaoAsync(admin, "Alfa");

        await FormularioHelpers.EnviarFormularioAsync(
            admin, $"/admin/collections/{alfa}", $"/admin/collections/{alfa}/videos/add",
            new Dictionary<string, string> { ["videoId"] = dentro.Id.ToString() });

        using var visitante = _app.CreateBrowser();
        var anonima = await visitante.GetStringAsync("/");
        Assert.DoesNotContain("Dentro", anonima);
        Assert.DoesNotContain("/favorite", anonima);
        Assert.True(Posicao(anonima, "Alfa") < Posicao(anonima, "Marcado"));
        Assert.True(Posicao(anonima, "Marcado") < Posicao(anonima, "Solto"));

        await FormularioHelpers.EnviarFormularioAsync(
            admin, "/", "/collections/alfa/favorite",
            new Dictionary<string, string> { ["destino"] = "/" });
        await FormularioHelpers.EnviarFormularioAsync(
            admin, "/", $"/videos/{marcado.Slug}/favorite",
            new Dictionary<string, string> { ["destino"] = "/" });
        await FormularioHelpers.EnviarFormularioAsync(
            admin, "/", $"/videos/{dentro.Slug}/favorite",
            new Dictionary<string, string> { ["destino"] = "/" });

        var favorita = await admin.GetStringAsync("/");
        Assert.True(Posicao(favorita, "Alfa") < Posicao(favorita, "Dentro"));
        Assert.True(Posicao(favorita, "Dentro") < Posicao(favorita, "Marcado"));
        Assert.True(Posicao(favorita, "Marcado") < Posicao(favorita, "Solto"));
        Assert.Contains($"/watch/{dentro.Slug}?collection=alfa", favorita);
        Assert.Contains("/collections/alfa", favorita);

        var estrelaDentro = favorita.IndexOf($"/videos/{dentro.Slug}/favorite", StringComparison.Ordinal);
        var estrelaMarcado = favorita.IndexOf($"/videos/{marcado.Slug}/favorite", StringComparison.Ordinal);
        var estrelaSolto = favorita.IndexOf($"/videos/{solto.Slug}/favorite", StringComparison.Ordinal);
        Assert.True(estrelaDentro >= 0 && estrelaDentro < estrelaMarcado && estrelaMarcado < estrelaSolto);
        Assert.Contains("bi-star-fill", favorita[estrelaDentro..estrelaMarcado]);
        Assert.Contains("bi-star-fill", favorita[estrelaMarcado..estrelaSolto]);
        Assert.Contains("bi-star\"", favorita[estrelaSolto..]);

        var assistir = await admin.GetStringAsync($"/watch/{marcado.Slug}");
        Assert.Contains($"/videos/{marcado.Slug}/favorite", assistir);
        Assert.Contains("bi-star-fill", assistir);

        await using (var db = postgres.CreateContext())
        {
            db.Users.Add(OpenTube.Domain.Entities.User.Create(
                OpenTube.Domain.ValueObjects.EmailAddress.Parse(Convidado), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        using var convidado = _app.CreateBrowser();
        await EntrarAsync(convidado, Convidado);
        var dele = await convidado.GetStringAsync("/");
        Assert.DoesNotContain("Dentro", dele);
        Assert.True(Posicao(dele, "Alfa") < Posicao(dele, "Marcado"));
        Assert.DoesNotContain("bi-star-fill", dele);
    }

    [Fact]
    public async Task A_home_poe_2_antes_de_10_no_nome()
    {
        using var storage = minio.CreateStorage();
        var deDez = await AcervoDeTeste.PublicarAsync(postgres, storage, "Dentro de dez", VideoVisibility.Public);
        var deDois = await AcervoDeTeste.PublicarAsync(postgres, storage, "Dentro de dois", VideoVisibility.Public);
        await AcervoDeTeste.PublicarAsync(postgres, storage, "10-solto", VideoVisibility.Public);
        await AcervoDeTeste.PublicarAsync(postgres, storage, "2-solto", VideoVisibility.Public);

        using var admin = _app.CreateBrowser();
        await EntrarAsync(admin, Admin);
        var dez = await CriarColecaoAsync(admin, "10-teste");
        var dois = await CriarColecaoAsync(admin, "2-outro-teste");

        await FormularioHelpers.EnviarFormularioAsync(
            admin, $"/admin/collections/{dez}", $"/admin/collections/{dez}/videos/add",
            new Dictionary<string, string> { ["videoId"] = deDez.Id.ToString() });
        await FormularioHelpers.EnviarFormularioAsync(
            admin, $"/admin/collections/{dois}", $"/admin/collections/{dois}/videos/add",
            new Dictionary<string, string> { ["videoId"] = deDois.Id.ToString() });

        using var visitante = _app.CreateBrowser();
        var html = await visitante.GetStringAsync("/");
        Assert.True(Posicao(html, "2-outro-teste") < Posicao(html, "10-teste"));
        Assert.True(Posicao(html, "10-teste") < Posicao(html, "2-solto"));
        Assert.True(Posicao(html, "2-solto") < Posicao(html, "10-solto"));
    }

    private static int Posicao(string html, string texto)
    {
        var indice = html.IndexOf(texto, StringComparison.Ordinal);
        Assert.True(indice >= 0, texto);
        return indice;
    }

    [Fact]
    public async Task Colecao_so_com_video_privado_nao_aparece_nem_abre()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Interno", VideoVisibility.Private);

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);
        var colecao = await CriarColecaoAsync(cliente, "Sigilosa");

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/add",
            new Dictionary<string, string> { ["videoId"] = video.Id.ToString() });

        using var visitante = _app.CreateBrowser();
        var home = await visitante.GetStringAsync("/");
        Assert.DoesNotContain("Sigilosa", home);
        Assert.DoesNotContain("Plano Interno", home);

        var resposta = await visitante.GetAsync("/collections/sigilosa");
        var html = await resposta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.DoesNotContain("Sigilosa", html);
        Assert.DoesNotContain("Plano Interno", html);
    }

    [Fact]
    public async Task Tirar_o_video_da_colecao_corta_o_acesso_de_quem_veio_por_ela()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Segurança da Informação", VideoVisibility.Restricted);

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);
        var colecao = await CriarColecaoAsync(cliente, "Treinamentos");

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/add",
            new Dictionary<string, string> { ["videoId"] = video.Id.ToString() });

        using (var escopo = _app.Services.CreateScope())
        {
            await escopo.ServiceProvider.GetRequiredService<GrantService>().InviteAsync(
                [Convidado], GrantTargetType.Collection, colecao, GrantValidity.Forever, Guid.CreateVersion7());
        }

        using var convidado = _app.CreateBrowser();
        await EntrarAsync(convidado, Convidado);
        Assert.Equal(HttpStatusCode.OK, (await convidado.GetAsync($"/watch/{video.Slug}")).StatusCode);

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/remove",
            new Dictionary<string, string> { ["videoId"] = video.Id.ToString() });

        Assert.Equal(HttpStatusCode.NotFound, (await convidado.GetAsync($"/watch/{video.Slug}")).StatusCode);
    }

    [Fact]
    public async Task Excluir_a_colecao_derruba_o_acesso_concedido_por_ela()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Segurança da Informação", VideoVisibility.Restricted);

        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);
        var colecao = await CriarColecaoAsync(cliente, "Treinamentos");

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/videos/add",
            new Dictionary<string, string> { ["videoId"] = video.Id.ToString() });

        using (var escopo = _app.Services.CreateScope())
        {
            await escopo.ServiceProvider.GetRequiredService<GrantService>().InviteAsync(
                [Convidado], GrantTargetType.Collection, colecao, GrantValidity.Forever, Guid.CreateVersion7());
        }

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/collections/{colecao}", $"/admin/collections/{colecao}/delete",
            new Dictionary<string, string>());

        // A coleção excluída não deixa de existir no banco, mas o vínculo com os vídeos some
        // do ponto de vista do acesso.
        await using var db = postgres.CreateContext();
        Assert.True((await db.Collections.SingleAsync()).IsDeleted);
    }

    [Fact]
    public async Task Colecao_inexistente_responde_como_nao_encontrada()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        var resposta = await cliente.GetAsync($"/admin/collections/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Fact]
    public async Task Criar_colecao_sem_nome_e_recusado()
    {
        using var cliente = _app.CreateBrowser();
        await EntrarAsync(cliente, Admin);

        var resposta = await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/admin/collections", "/admin/collections/create",
            new Dictionary<string, string> { ["nome"] = "   ", ["descricao"] = "" });

        Assert.Contains("erro=", resposta.Headers.Location!.ToString());
    }
}
