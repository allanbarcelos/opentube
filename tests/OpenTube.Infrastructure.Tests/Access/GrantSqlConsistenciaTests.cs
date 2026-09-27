// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Access;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Access;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Playback;
using OpenTube.Infrastructure.Security;
using OpenTube.Infrastructure.Storage;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Access;

/// <summary>
/// A regra de concessão existe duas vezes: em C#, para decidir uma reprodução, e em SQL, para
/// filtrar listagem e busca sem trazer o acervo inteiro. Esta suíte percorre uma matriz de
/// casos e exige que as duas concordem — é o que impede que um vídeo restrito apareça na home
/// por divergência entre as implementações.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class GrantSqlConsistenciaTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Segredo = "segredo-de-teste";
    private const string Token = "token-do-link";

    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();
    private static readonly EmailAddress Allan = EmailAddress.Parse("allan@barcelos.dev");

    private readonly FakeTimeProvider _relogio = new(Agora);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Quem pede o vídeo em cada caso.</summary>
    public enum Espectador
    {
        Anonimo,
        PessoaDoDominio,
        PessoaDeFora,
        ComLink
    }

    /// <summary>Como a concessão foi montada em cada caso.</summary>
    public enum Cenario
    {
        SemConcessao,
        ParaAPessoa,
        ParaOutraPessoa,
        ParaODominio,
        ParaOutroDominio,
        Publica,
        PorLink,
        SobreAColecao,
        SobreOutraColecao,
        SobreTodoOAcervo,
        AindaNaoComecou,
        JaExpirou,
        Revogada,
        Esgotada,
        PrazoRelativoEmCurso,
        PrazoRelativoVencido
    }

    public static TheoryData<Cenario, Espectador> Casos()
    {
        var dados = new TheoryData<Cenario, Espectador>();

        foreach (var cenario in Enum.GetValues<Cenario>())
            foreach (var espectador in Enum.GetValues<Espectador>())
                dados.Add(cenario, espectador);

        return dados;
    }

    [Theory]
    [MemberData(nameof(Casos))]
    public async Task A_listagem_e_a_reproducao_concordam(Cenario cenario, Espectador quem)
    {
        var video = await CriarVideoRestritoAsync();
        var colecao = await CriarColecaoAsync(video.Id);
        var concessao = MontarConcessao(cenario, video.Id, colecao);

        if (concessao is not null)
            await GravarAsync(concessao, cenario);

        var espectador = await MontarEspectadorAsync(quem);

        await using var db = postgres.CreateContext();
        var acesso = CriarAcesso(db);
        var catalogo = new VideoCatalog(db, acesso, _relogio);

        var decisaoDeReproducao = (await acesso.EvaluateAsync(espectador, video)).Allowed;
        var aparece = (await catalogo.BrowseAsync(espectador)).Items.Any(v => v.Id == video.Id);

        Assert.True(
            decisaoDeReproducao == aparece,
            $"divergência em {cenario}/{quem}: reprodução={decisaoDeReproducao}, listagem={aparece}");
    }

    [Theory]
    [MemberData(nameof(Casos))]
    public async Task A_busca_respeita_a_mesma_decisao(Cenario cenario, Espectador quem)
    {
        var video = await CriarVideoRestritoAsync();
        var colecao = await CriarColecaoAsync(video.Id);
        var concessao = MontarConcessao(cenario, video.Id, colecao);

        if (concessao is not null)
            await GravarAsync(concessao, cenario);

        var espectador = await MontarEspectadorAsync(quem);

        await using var db = postgres.CreateContext();
        var acesso = CriarAcesso(db);
        var catalogo = new VideoCatalog(db, acesso, _relogio);

        var decisaoDeReproducao = (await acesso.EvaluateAsync(espectador, video)).Allowed;
        var apareceNaBusca = (await catalogo.BrowseAsync(espectador, "confidencial")).Items.Any(v => v.Id == video.Id);

        Assert.True(
            decisaoDeReproducao == apareceNaBusca,
            $"divergência na busca em {cenario}/{quem}: reprodução={decisaoDeReproducao}, busca={apareceNaBusca}");
    }

    private AccessService CriarAcesso(OpenTubeDbContext db) =>
        new(db, Microsoft.Extensions.Options.Options.Create(
            new SecurityOptions { TokenPepper = Segredo, IpHashPepper = Segredo }), _relogio);

    private async Task<Video> CriarVideoRestritoAsync()
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

    private async Task<Guid> CriarColecaoAsync(Guid videoId)
    {
        await using var db = postgres.CreateContext();

        var colecao = Collection.Create("Treinamentos", $"tre-{Guid.CreateVersion7():n}"[..20], Admin, Agora);
        colecao.Add(videoId);

        db.Collections.Add(colecao);
        await db.SaveChangesAsync();

        return colecao.Id;
    }

    private static AccessGrant? MontarConcessao(Cenario cenario, Guid videoId, Guid colecaoId) => cenario switch
    {
        Cenario.SemConcessao => null,
        Cenario.ParaAPessoa => AccessGrant.ForUser(Allan, GrantTargetType.Video, videoId, Admin, Agora),
        Cenario.ParaOutraPessoa => AccessGrant.ForUser(
            EmailAddress.Parse("outra@barcelos.dev"), GrantTargetType.Video, videoId, Admin, Agora),
        Cenario.ParaODominio => AccessGrant.ForDomain("barcelos.dev", GrantTargetType.Video, videoId, Admin, Agora),
        Cenario.ParaOutroDominio => AccessGrant.ForDomain("outra.com", GrantTargetType.Video, videoId, Admin, Agora),
        Cenario.Publica => AccessGrant.Create(GrantSubjectType.Public, null, GrantTargetType.Video, videoId, Admin, Agora),
        Cenario.PorLink => AccessGrant.ForLink(
            TokenHasher.Hash(Token, Segredo), GrantTargetType.Video, videoId, Admin, Agora),
        Cenario.SobreAColecao => AccessGrant.ForUser(Allan, GrantTargetType.Collection, colecaoId, Admin, Agora),
        Cenario.SobreOutraColecao => AccessGrant.ForUser(
            Allan, GrantTargetType.Collection, Guid.CreateVersion7(), Admin, Agora),
        Cenario.SobreTodoOAcervo => AccessGrant.ForUser(Allan, GrantTargetType.All, null, Admin, Agora),
        Cenario.AindaNaoComecou => AccessGrant.Create(
            GrantSubjectType.User, Allan.Value, GrantTargetType.Video, videoId, Admin, Agora,
            startsAt: Agora.AddDays(5)),
        Cenario.JaExpirou => AccessGrant.ForUser(
            Allan, GrantTargetType.Video, videoId, Admin, Agora, expiresAt: Agora.AddDays(-1)),
        Cenario.Revogada => AccessGrant.ForUser(Allan, GrantTargetType.Video, videoId, Admin, Agora),
        Cenario.Esgotada => AccessGrant.Create(
            GrantSubjectType.User, Allan.Value, GrantTargetType.Video, videoId, Admin, Agora, maxViews: 1),
        Cenario.PrazoRelativoEmCurso => AccessGrant.ForUser(
            Allan, GrantTargetType.Video, videoId, Admin, Agora, durationAfterFirstUse: TimeSpan.FromDays(30)),
        Cenario.PrazoRelativoVencido => AccessGrant.ForUser(
            Allan, GrantTargetType.Video, videoId, Admin, Agora, durationAfterFirstUse: TimeSpan.FromDays(30)),
        _ => throw new ArgumentOutOfRangeException(nameof(cenario))
    };

    private async Task GravarAsync(AccessGrant concessao, Cenario cenario)
    {
        switch (cenario)
        {
            case Cenario.Revogada:
                concessao.Revoke(Agora);
                break;

            case Cenario.Esgotada:
                concessao.RegisterUse(Agora);
                break;

            case Cenario.PrazoRelativoEmCurso:
                concessao.RegisterUse(Agora.AddDays(-1));
                break;

            case Cenario.PrazoRelativoVencido:
                concessao.RegisterUse(Agora.AddDays(-40));
                break;
        }

        await using var db = postgres.CreateContext();
        db.AccessGrants.Add(concessao);
        await db.SaveChangesAsync();
    }

    private async Task<Viewer> MontarEspectadorAsync(Espectador quem)
    {
        switch (quem)
        {
            case Espectador.PessoaDoDominio:
                return Viewer.Authenticated(Guid.CreateVersion7(), Allan);

            case Espectador.PessoaDeFora:
                return Viewer.Authenticated(Guid.CreateVersion7(), EmailAddress.Parse("estranho@outra.com"));

            case Espectador.ComLink:
                await using (var db = postgres.CreateContext())
                    return await CriarAcesso(db).ResolveLinkAsync(Viewer.Anonymous, Token);

            default:
                return Viewer.Anonymous;
        }
    }
}
