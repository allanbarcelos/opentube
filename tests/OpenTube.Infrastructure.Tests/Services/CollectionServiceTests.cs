// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Services;
using OpenTube.Infrastructure.Storage;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Services;

[Collection(IntegrationCollection.Name)]
public class CollectionServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();

    private readonly FakeTimeProvider _relogio = new(Agora);
    private readonly IVideoStorage _storage = Substitute.For<IVideoStorage>();

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private (CollectionService Servico, OpenTubeDbContext Db) Criar()
    {
        var db = postgres.CreateContext();

        return (new CollectionService(db, _relogio, _storage, NullLogger<CollectionService>.Instance), db);
    }

    private async Task<Video> CriarVideoAsync(string titulo)
    {
        await using var db = postgres.CreateContext();
        var videoId = Guid.CreateVersion7();

        var video = Video.CreateDraft(titulo, $"v-{videoId:n}"[..20], "originals/a.mp4", Admin, Agora, id: videoId);
        video.MarkUploaded(1024);
        video.StartProcessing();
        video.MarkReady(StorageKeys.VodPrefix(videoId), 60, 640, 360, null, null, Agora);

        db.Videos.Add(video);
        await db.SaveChangesAsync();

        return video;
    }

    [Fact]
    public async Task Cria_colecao_com_endereco_derivado_do_nome()
    {
        var (servico, db) = Criar();
        await using var _ = db;

        var colecao = await servico.CreateAsync("Treinamentos Obrigatórios", "Para toda a equipe", Admin);

        Assert.Equal("treinamentos-obrigatorios", colecao.Slug);
        Assert.Equal("Para toda a equipe", colecao.Description);
        Assert.Empty(colecao.Videos);
    }

    [Fact]
    public async Task Nomes_repetidos_geram_enderecos_diferentes()
    {
        var (servico, db) = Criar();
        await using var _ = db;

        await servico.CreateAsync("Treinamentos", null, Admin);
        var segunda = await servico.CreateAsync("Treinamentos", null, Admin);

        Assert.Equal("treinamentos-2", segunda.Slug);
    }

    [Fact]
    public async Task Exige_nome()
    {
        var (servico, db) = Criar();
        await using var _ = db;

        await Assert.ThrowsAnyAsync<ArgumentException>(() => servico.CreateAsync("   ", null, Admin));
    }

    [Fact]
    public async Task Renomear_nao_altera_o_endereco()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var colecao = await servico.CreateAsync("Treinamentos", null, Admin);

        await servico.RenameAsync(colecao.Id, "Capacitação 2026", "Nova descrição");

        await using var leitura = postgres.CreateContext();
        var lida = await leitura.Collections.SingleAsync();

        Assert.Equal("Capacitação 2026", lida.Name);
        Assert.Equal("treinamentos", lida.Slug);
    }

    [Fact]
    public async Task Acrescenta_e_remove_videos()
    {
        var a = await CriarVideoAsync("Primeiro");
        var b = await CriarVideoAsync("Segundo");
        var (servico, db) = Criar();
        await using var _ = db;
        var colecao = await servico.CreateAsync("Treinamentos", null, Admin);

        await servico.AddVideoAsync(colecao.Id, a.Id);
        await servico.AddVideoAsync(colecao.Id, b.Id);
        await servico.RemoveVideoAsync(colecao.Id, a.Id);

        var videos = await servico.VideosOfAsync(colecao.Id);

        Assert.Equal(["Segundo"], videos.Select(v => v.Title));
    }

    [Fact]
    public async Task Acrescentar_o_mesmo_video_duas_vezes_nao_duplica()
    {
        var video = await CriarVideoAsync("Primeiro");
        var (servico, db) = Criar();
        await using var _ = db;
        var colecao = await servico.CreateAsync("Treinamentos", null, Admin);

        await servico.AddVideoAsync(colecao.Id, video.Id);
        await servico.AddVideoAsync(colecao.Id, video.Id);

        Assert.Single(await servico.VideosOfAsync(colecao.Id));
    }

    [Fact]
    public async Task Nao_acrescenta_video_inexistente()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var colecao = await servico.CreateAsync("Treinamentos", null, Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servico.AddVideoAsync(colecao.Id, Guid.CreateVersion7()));
    }

    [Fact]
    public async Task Redefinir_o_conteudo_respeita_a_ordem_pedida()
    {
        var a = await CriarVideoAsync("Primeiro");
        var b = await CriarVideoAsync("Segundo");
        var c = await CriarVideoAsync("Terceiro");
        var (servico, db) = Criar();
        await using var _ = db;
        var colecao = await servico.CreateAsync("Treinamentos", null, Admin);

        await servico.SetVideosAsync(colecao.Id, [c.Id, a.Id, b.Id]);

        var videos = await servico.VideosOfAsync(colecao.Id);

        Assert.Equal(["Terceiro", "Primeiro", "Segundo"], videos.Select(v => v.Title));
    }

    [Fact]
    public async Task Redefinir_descarta_identificadores_que_nao_existem()
    {
        var a = await CriarVideoAsync("Primeiro");
        var (servico, db) = Criar();
        await using var _ = db;
        var colecao = await servico.CreateAsync("Treinamentos", null, Admin);

        await servico.SetVideosAsync(colecao.Id, [Guid.CreateVersion7(), a.Id]);

        Assert.Single(await servico.VideosOfAsync(colecao.Id));
    }

    [Fact]
    public async Task Video_excluido_some_da_colecao()
    {
        var video = await CriarVideoAsync("Primeiro");
        var (servico, db) = Criar();
        await using var _ = db;
        var colecao = await servico.CreateAsync("Treinamentos", null, Admin);
        await servico.AddVideoAsync(colecao.Id, video.Id);

        await using (var outro = postgres.CreateContext())
        {
            var alvo = await outro.Videos.SingleAsync(v => v.Id == video.Id);
            alvo.SoftDelete(Agora);
            await outro.SaveChangesAsync();
        }

        Assert.Empty(await servico.VideosOfAsync(colecao.Id));
    }

    [Fact]
    public async Task A_listagem_traz_as_contagens_de_video_e_de_concessao()
    {
        var video = await CriarVideoAsync("Primeiro");
        var (servico, db) = Criar();
        await using var _ = db;
        var colecao = await servico.CreateAsync("Treinamentos", null, Admin);
        await servico.AddVideoAsync(colecao.Id, video.Id);

        await using (var outro = postgres.CreateContext())
        {
            outro.AccessGrants.Add(AccessGrant.ForUser(
                OpenTube.Domain.ValueObjects.EmailAddress.Parse("allan@barcelos.dev"),
                GrantTargetType.Collection, colecao.Id, Admin, Agora));
            await outro.SaveChangesAsync();
        }

        var listagem = await servico.ListAsync();
        var resumo = Assert.Single(listagem);

        Assert.Equal(1, resumo.VideoCount);
        Assert.Equal(1, resumo.GrantCount);
        Assert.False(resumo.IsDeleted);
    }

    [Fact]
    public async Task Concessao_revogada_nao_entra_na_contagem()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var colecao = await servico.CreateAsync("Treinamentos", null, Admin);

        await using (var outro = postgres.CreateContext())
        {
            var concessao = AccessGrant.ForUser(
                OpenTube.Domain.ValueObjects.EmailAddress.Parse("allan@barcelos.dev"),
                GrantTargetType.Collection, colecao.Id, Admin, Agora);
            concessao.Revoke(Agora);
            outro.AccessGrants.Add(concessao);
            await outro.SaveChangesAsync();
        }

        Assert.Equal(0, (await servico.ListAsync()).Single().GrantCount);
    }

    [Fact]
    public async Task Excluir_apaga_a_colecao_de_vez()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var colecao = await servico.CreateAsync("Treinamentos", null, Admin);

        await servico.DeleteAsync(colecao.Id, VideosDaColecao.Desvincular, null);

        Assert.Empty(await servico.ListAsync());
        Assert.Empty(await servico.ListAsync(includeDeleted: true));
        Assert.Null(await servico.FindAsync(colecao.Id));
    }

    [Fact]
    public async Task Recusa_operar_sobre_colecao_inexistente()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var inexistente = Guid.CreateVersion7();

        await Assert.ThrowsAsync<InvalidOperationException>(() => servico.RenameAsync(inexistente, "x", null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servico.DeleteAsync(inexistente, VideosDaColecao.Desvincular, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => servico.SetVideosAsync(inexistente, []));
    }

    [Fact]
    public async Task Encontra_a_colecao_com_os_videos()
    {
        var video = await CriarVideoAsync("Primeiro");
        var (servico, db) = Criar();
        await using var _ = db;
        var colecao = await servico.CreateAsync("Treinamentos", null, Admin);
        await servico.AddVideoAsync(colecao.Id, video.Id);

        await using var leitura = postgres.CreateContext();
        var servicoDeLeitura = new CollectionService(leitura, _relogio, _storage, NullLogger<CollectionService>.Instance);

        var encontrada = await servicoDeLeitura.FindAsync(colecao.Id);

        Assert.NotNull(encontrada);
        Assert.Single(encontrada.Videos);
        Assert.Null(await servicoDeLeitura.FindAsync(Guid.CreateVersion7()));
    }

    [Fact]
    public async Task Video_em_outra_colecao_nao_muda_sem_confirmacao()
    {
        var video = await CriarVideoAsync("Primeiro");
        var (servico, db) = Criar();
        await using var _ = db;
        var origem = await servico.CreateAsync("Alfa", null, Admin);
        var destino = await servico.CreateAsync("Beta", null, Admin);
        await servico.AddVideoAsync(origem.Id, video.Id);

        var resultado = await servico.AddVideoAsync(destino.Id, video.Id);

        Assert.True(resultado.NeedsConfirmation);
        Assert.False(resultado.Moved);
        Assert.Equal(["Alfa"], resultado.OtherCollections);

        var vinculo = await db.CollectionVideos.AsNoTracking().SingleAsync();
        Assert.Equal(origem.Id, vinculo.CollectionId);
        Assert.Null(await servico.FindPlacementConflictAsync(origem.Id, video.Id));

        var conflito = await servico.FindPlacementConflictAsync(destino.Id, video.Id);
        Assert.NotNull(conflito);
        Assert.Equal("Primeiro", conflito.Title);
        Assert.Equal("Alfa", conflito.CollectionNames);
    }

    [Fact]
    public async Task Confirmacao_move_o_video_e_marca_a_chegada()
    {
        var video = await CriarVideoAsync("Primeiro");
        var (servico, db) = Criar();
        await using var _ = db;
        var origem = await servico.CreateAsync("Alfa", null, Admin);
        var destino = await servico.CreateAsync("Beta", null, Admin);
        await servico.AddVideoAsync(origem.Id, video.Id);
        _relogio.Advance(TimeSpan.FromHours(2));

        var resultado = await servico.AddVideoAsync(destino.Id, video.Id, confirmMove: true);

        Assert.True(resultado.Moved);
        Assert.Equal(["Alfa"], resultado.OtherCollections);

        var vinculo = await db.CollectionVideos.AsNoTracking().SingleAsync();
        Assert.Equal(destino.Id, vinculo.CollectionId);
        Assert.Equal(Agora.AddHours(2), vinculo.AddedAt);
        Assert.Equal(["Primeiro"], (await servico.VideosOfAsync(destino.Id)).Select(v => v.Title));
        Assert.Empty(await servico.VideosOfAsync(origem.Id));
    }

    [Fact]
    public async Task Excluir_desvincula_os_videos_e_apaga_os_acessos_da_colecao()
    {
        var video = await CriarVideoAsync("Primeiro");
        var (servico, db) = Criar();
        await using var _ = db;
        var colecao = await servico.CreateAsync("Alfa", null, Admin);
        await servico.AddVideoAsync(colecao.Id, video.Id);

        var usuario = User.Create(EmailAddress.Parse("ana@empresa.com"), Agora);
        db.Users.Add(usuario);
        db.CollectionFavorites.Add(CollectionFavorite.Mark(colecao.Id, usuario.Id, Agora));
        db.AccessGrants.Add(AccessGrant.ForUser(
            EmailAddress.Parse("allan@barcelos.dev"), GrantTargetType.Collection, colecao.Id, Admin, Agora));
        db.AccessGrants.Add(AccessGrant.ForUser(
            EmailAddress.Parse("allan@barcelos.dev"), GrantTargetType.Video, video.Id, Admin, Agora));
        db.Invitations.Add(Invitation.Create(InvitationKind.People, GrantTargetType.Collection, colecao.Id, Admin, Agora));
        db.Invitations.Add(Invitation.Create(InvitationKind.People, GrantTargetType.Video, video.Id, Admin, Agora));
        await db.SaveChangesAsync();

        var exclusao = await servico.DeleteAsync(colecao.Id, VideosDaColecao.Desvincular, null);

        Assert.Equal(VideosDaColecao.Desvincular, exclusao.Videos);
        Assert.Empty(exclusao.DeletedVideoIds);
        Assert.Empty(await db.Collections.AsNoTracking().ToListAsync());
        Assert.Empty(await db.CollectionVideos.AsNoTracking().ToListAsync());
        Assert.Empty(await db.CollectionFavorites.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Invitations.AsNoTracking().Where(i => i.TargetType == GrantTargetType.Collection).ToListAsync());

        var concessao = await db.AccessGrants.AsNoTracking().SingleAsync();
        Assert.Equal(GrantTargetType.Video, concessao.TargetType);
        Assert.Equal(video.Id, concessao.TargetId);

        var convite = await db.Invitations.AsNoTracking().SingleAsync();
        Assert.Equal(video.Id, convite.TargetId);

        var restante = await db.Videos.AsNoTracking().SingleAsync();
        Assert.Null(restante.DeletedAt);
    }

    [Fact]
    public async Task Excluir_os_videos_junto_apaga_cada_um_de_vez()
    {
        var video = await CriarVideoAsync("Primeiro");
        var outro = await CriarVideoAsync("Segundo");
        var (servico, db) = Criar();
        await using var _ = db;
        var colecao = await servico.CreateAsync("Alfa", null, Admin);
        await servico.AddVideoAsync(colecao.Id, video.Id);
        await servico.AddVideoAsync(colecao.Id, outro.Id);
        _storage.ClearReceivedCalls();

        var exclusao = await servico.DeleteAsync(colecao.Id, VideosDaColecao.Excluir, null);

        Assert.Equal(
            new[] { video.Id, outro.Id }.OrderBy(id => id),
            exclusao.DeletedVideoIds.OrderBy(id => id));
        Assert.Empty(await db.Collections.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Videos.AsNoTracking().ToListAsync());
        Assert.Empty(await db.CollectionVideos.AsNoTracking().ToListAsync());
        await _storage.Received(1).DeletePrefixAsync(StorageBucket.Vod, StorageKeys.VodPrefix(video.Id), Arg.Any<CancellationToken>());
        await _storage.Received(1).DeletePrefixAsync(StorageBucket.Originals, StorageKeys.VodPrefix(outro.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Excluir_movendo_os_videos_para_outra_colecao()
    {
        var video = await CriarVideoAsync("Primeiro");
        var (servico, db) = Criar();
        await using var _ = db;
        var origem = await servico.CreateAsync("Alfa", null, Admin);
        var destino = await servico.CreateAsync("Beta", null, Admin);
        await servico.AddVideoAsync(origem.Id, video.Id);
        _relogio.Advance(TimeSpan.FromHours(3));

        var exclusao = await servico.DeleteAsync(origem.Id, VideosDaColecao.Mover, destino.Id);

        Assert.Equal("Beta", exclusao.DestinationName);
        Assert.Empty(exclusao.DeletedVideoIds);
        Assert.Null(await db.Collections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == origem.Id));

        var vinculo = await db.CollectionVideos.AsNoTracking().SingleAsync();
        Assert.Equal(destino.Id, vinculo.CollectionId);
        Assert.Equal(Agora.AddHours(3), vinculo.AddedAt);
    }

    [Fact]
    public async Task Mover_sem_outra_colecao_nao_apaga_nada()
    {
        var video = await CriarVideoAsync("Primeiro");
        var (servico, db) = Criar();
        await using var _ = db;
        var origem = await servico.CreateAsync("Alfa", null, Admin);
        await servico.AddVideoAsync(origem.Id, video.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servico.DeleteAsync(origem.Id, VideosDaColecao.Mover, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servico.DeleteAsync(origem.Id, VideosDaColecao.Mover, origem.Id));

        Assert.Equal(origem.Id, (await db.CollectionVideos.AsNoTracking().SingleAsync()).CollectionId);
        Assert.NotNull(await servico.FindAsync(origem.Id));
    }

    [Fact]
    public async Task Excluir_apaga_a_capa_no_storage()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var colecao = await servico.CreateAsync("Alfa", null, Admin);
        colecao.SetThumbnail("collections/alfa/thumb-1.jpg", 1);
        await db.SaveChangesAsync();

        await servico.DeleteAsync(colecao.Id, VideosDaColecao.Desvincular, null);

        await _storage.Received(1).DeleteKeysAsync(
            StorageBucket.Vod,
            Arg.Is<IEnumerable<string>>(chaves => chaves.Single() == "collections/alfa/thumb-1.jpg"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Redefinir_tira_o_video_da_colecao_anterior()
    {
        var video = await CriarVideoAsync("Primeiro");
        var (servico, db) = Criar();
        await using var _ = db;
        var origem = await servico.CreateAsync("Alfa", null, Admin);
        var destino = await servico.CreateAsync("Beta", null, Admin);
        await servico.AddVideoAsync(origem.Id, video.Id);

        await servico.SetVideosAsync(destino.Id, [video.Id]);

        var vinculo = await db.CollectionVideos.AsNoTracking().SingleAsync();
        Assert.Equal(destino.Id, vinculo.CollectionId);
        Assert.Empty(await servico.VideosOfAsync(origem.Id));
    }

    [Fact]
    public async Task O_banco_recusa_o_mesmo_video_em_duas_colecoes()
    {
        var video = await CriarVideoAsync("Primeiro");
        var (servico, db) = Criar();
        await using var _ = db;
        var origem = await servico.CreateAsync("Alfa", null, Admin);
        var destino = await servico.CreateAsync("Beta", null, Admin);
        await servico.AddVideoAsync(origem.Id, video.Id);

        await using var outro = postgres.CreateContext();
        var beta = await outro.Collections.Include(c => c.Videos).SingleAsync(c => c.Id == destino.Id);
        beta.Add(video.Id, Agora);

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => outro.SaveChangesAsync());
    }
}
