// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Access;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Access;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Security;
using OpenTube.Infrastructure.Storage;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Access;

[Collection(IntegrationCollection.Name)]
public class AccessServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Segredo = "segredo-de-teste";
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();
    private static readonly EmailAddress Allan = EmailAddress.Parse("allan@barcelos.dev");

    private readonly FakeTimeProvider _relogio = new(Agora);

    private static readonly Viewer Convidado = Viewer.Authenticated(Guid.CreateVersion7(), Allan);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private AccessService Criar(OpenTubeDbContext db) =>
        new(db, Microsoft.Extensions.Options.Options.Create(
            new SecurityOptions { TokenPepper = Segredo, IpHashPepper = Segredo }), _relogio);

    private async Task<Video> VideoRestritoAsync()
    {
        await using var db = postgres.CreateContext();
        var videoId = Guid.CreateVersion7();

        var video = Video.CreateDraft("Confidencial", $"conf-{videoId:n}"[..20], "originals/a.mp4", Admin, Agora, id: videoId);
        video.MarkUploaded(1024);
        video.StartProcessing();
        video.MarkReady(StorageKeys.VodPrefix(videoId), 120, 1280, 720, null, null, Agora);
        video.ChangeVisibility(VideoVisibility.Restricted);

        db.Videos.Add(video);
        await db.SaveChangesAsync();

        return video;
    }

    private async Task<AccessGrant> GravarAsync(AccessGrant concessao)
    {
        await using var db = postgres.CreateContext();
        db.AccessGrants.Add(concessao);
        await db.SaveChangesAsync();

        return concessao;
    }

    [Fact]
    public async Task Sem_concessao_o_acesso_e_negado()
    {
        var video = await VideoRestritoAsync();
        await using var db = postgres.CreateContext();

        var resultado = await Criar(db).EvaluateAsync(Convidado, video);

        Assert.False(resultado.Allowed);
        Assert.Equal(AccessReason.NoGrant, resultado.Reason);
        Assert.Null(resultado.GrantId);
    }

    [Fact]
    public async Task Concessao_para_a_pessoa_libera_e_identifica_a_origem()
    {
        var video = await VideoRestritoAsync();
        var concessao = await GravarAsync(AccessGrant.ForUser(Allan, GrantTargetType.Video, video.Id, Admin, Agora));

        await using var db = postgres.CreateContext();
        var resultado = await Criar(db).EvaluateAsync(Convidado, video);

        Assert.True(resultado.Allowed);
        Assert.Equal(AccessReason.GrantedToUser, resultado.Reason);
        Assert.Equal(concessao.Id, resultado.GrantId);
    }

    [Fact]
    public async Task Concessao_por_dominio_alcanca_quem_tem_email_do_dominio()
    {
        var video = await VideoRestritoAsync();
        await GravarAsync(AccessGrant.ForDomain("barcelos.dev", GrantTargetType.All, null, Admin, Agora));

        await using var db = postgres.CreateContext();
        var resultado = await Criar(db).EvaluateAsync(Convidado, video);

        Assert.True(resultado.Allowed);
        Assert.Equal(AccessReason.GrantedToDomain, resultado.Reason);
    }

    [Fact]
    public async Task Concessao_por_colecao_alcanca_o_video_dentro_dela()
    {
        var video = await VideoRestritoAsync();
        var colecao = Collection.Create("Treinamentos", "treinamentos", Admin, Agora);
        colecao.Add(video.Id);

        await using (var db = postgres.CreateContext())
        {
            db.Collections.Add(colecao);
            await db.SaveChangesAsync();
        }

        await GravarAsync(AccessGrant.ForUser(Allan, GrantTargetType.Collection, colecao.Id, Admin, Agora));

        await using var leitura = postgres.CreateContext();
        Assert.True((await Criar(leitura).EvaluateAsync(Convidado, video)).Allowed);
    }

    [Fact]
    public async Task Tirar_o_video_da_colecao_corta_o_acesso()
    {
        var video = await VideoRestritoAsync();
        var colecao = Collection.Create("Treinamentos", "treinamentos", Admin, Agora);
        colecao.Add(video.Id);

        await using (var db = postgres.CreateContext())
        {
            db.Collections.Add(colecao);
            await db.SaveChangesAsync();
        }

        await GravarAsync(AccessGrant.ForUser(Allan, GrantTargetType.Collection, colecao.Id, Admin, Agora));

        await using (var db = postgres.CreateContext())
        {
            var alvo = await db.Collections.Include(c => c.Videos).SingleAsync(c => c.Id == colecao.Id);
            alvo.Remove(video.Id);
            await db.SaveChangesAsync();
        }

        await using var leitura = postgres.CreateContext();
        Assert.False((await Criar(leitura).EvaluateAsync(Convidado, video)).Allowed);
    }

    [Fact]
    public async Task O_token_do_link_e_conferido_pelo_resumo()
    {
        var video = await VideoRestritoAsync();
        const string token = "token-secreto-do-link";
        await GravarAsync(AccessGrant.ForLink(
            TokenHasher.Hash(token, Segredo), GrantTargetType.Video, video.Id, Admin, Agora));

        await using var db = postgres.CreateContext();
        var servico = Criar(db);

        var comLink = await servico.ResolveLinkAsync(Viewer.Anonymous, token);

        Assert.NotNull(comLink.LinkGrantId);
        Assert.True((await servico.EvaluateAsync(comLink, video)).Allowed);
    }

    [Fact]
    public async Task O_segredo_do_link_nao_e_guardado_em_texto_claro()
    {
        var video = await VideoRestritoAsync();
        const string token = "token-secreto-do-link";
        await GravarAsync(AccessGrant.ForLink(
            TokenHasher.Hash(token, Segredo), GrantTargetType.Video, video.Id, Admin, Agora));

        await using var db = postgres.CreateContext();
        var guardado = await db.AccessGrants.Select(g => g.SubjectValue).SingleAsync();

        Assert.DoesNotContain(token, guardado);
    }

    [Theory]
    [InlineData("token-errado")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Token_invalido_nao_produz_acesso(string? token)
    {
        var video = await VideoRestritoAsync();
        await GravarAsync(AccessGrant.ForLink(
            TokenHasher.Hash("token-certo", Segredo), GrantTargetType.Video, video.Id, Admin, Agora));

        await using var db = postgres.CreateContext();
        var servico = Criar(db);

        var comLink = await servico.ResolveLinkAsync(Viewer.Anonymous, token);

        Assert.Null(comLink.LinkGrantId);
        Assert.False((await servico.EvaluateAsync(comLink, video)).Allowed);
    }

    [Fact]
    public async Task Registrar_o_uso_dispara_a_contagem_do_prazo_relativo()
    {
        var video = await VideoRestritoAsync();
        var concessao = await GravarAsync(AccessGrant.ForUser(
            Allan, GrantTargetType.Video, video.Id, Admin, Agora, durationAfterFirstUse: TimeSpan.FromDays(7)));

        await using (var db = postgres.CreateContext())
            await Criar(db).RegisterUseAsync(concessao.Id);

        await using var leitura = postgres.CreateContext();
        var gravada = await leitura.AccessGrants.SingleAsync(g => g.Id == concessao.Id);

        Assert.Equal(Agora, gravada.FirstUsedAt);
        Assert.Equal(1, gravada.ViewsUsed);
        Assert.Equal(Agora.AddDays(7), gravada.EffectiveExpiry);
    }

    [Fact]
    public async Task O_acesso_expira_depois_do_prazo_contado_do_primeiro_uso()
    {
        var video = await VideoRestritoAsync();
        var concessao = await GravarAsync(AccessGrant.ForUser(
            Allan, GrantTargetType.Video, video.Id, Admin, Agora, durationAfterFirstUse: TimeSpan.FromDays(7)));

        await using (var db = postgres.CreateContext())
            await Criar(db).RegisterUseAsync(concessao.Id);

        _relogio.Advance(TimeSpan.FromDays(8));

        await using var leitura = postgres.CreateContext();
        var resultado = await Criar(leitura).EvaluateAsync(Convidado, video);

        Assert.False(resultado.Allowed);
        Assert.Equal(AccessReason.GrantExpired, resultado.Reason);
    }

    [Fact]
    public async Task Limite_de_visualizacoes_esgota_o_acesso()
    {
        var video = await VideoRestritoAsync();
        var concessao = await GravarAsync(AccessGrant.ForLink(
            TokenHasher.Hash("token", Segredo), GrantTargetType.Video, video.Id, Admin, Agora, maxViews: 2));

        await using var db = postgres.CreateContext();
        var servico = Criar(db);
        var espectador = await servico.ResolveLinkAsync(Viewer.Anonymous, "token");

        for (var i = 0; i < 2; i++)
        {
            Assert.True((await servico.EvaluateAsync(espectador, video)).Allowed);
            await servico.RegisterUseAsync(concessao.Id);
        }

        var resultado = await servico.EvaluateAsync(espectador, video);

        Assert.False(resultado.Allowed);
        Assert.Equal(AccessReason.GrantExhausted, resultado.Reason);
    }

    [Fact]
    public async Task Revogar_corta_o_acesso_na_proxima_avaliacao()
    {
        var video = await VideoRestritoAsync();
        var concessao = await GravarAsync(AccessGrant.ForUser(Allan, GrantTargetType.Video, video.Id, Admin, Agora));

        await using (var db = postgres.CreateContext())
        {
            var alvo = await db.AccessGrants.SingleAsync(g => g.Id == concessao.Id);
            alvo.Revoke(Agora);
            await db.SaveChangesAsync();
        }

        await using var leitura = postgres.CreateContext();
        var resultado = await Criar(leitura).EvaluateAsync(Convidado, video);

        Assert.False(resultado.Allowed);
        Assert.Equal(AccessReason.GrantRevoked, resultado.Reason);
    }

    [Fact]
    public async Task Registrar_uso_de_concessao_inexistente_nao_quebra()
    {
        await using var db = postgres.CreateContext();

        await Criar(db).RegisterUseAsync(Guid.CreateVersion7());
    }

    [Fact]
    public async Task Video_publico_dispensa_consulta_a_concessoes()
    {
        await using var db = postgres.CreateContext();
        var videoId = Guid.CreateVersion7();

        var video = Video.CreateDraft("Público", "publico", "originals/a.mp4", Admin, Agora, id: videoId);
        video.MarkUploaded(1024);
        video.StartProcessing();
        video.MarkReady("vod/a/", 10, 640, 360, null, null, Agora);
        video.ChangeVisibility(VideoVisibility.Public);

        var resultado = await Criar(db).EvaluateAsync(Viewer.Anonymous, video);

        Assert.True(resultado.Allowed);
        Assert.Null(resultado.GrantId);
    }
}
