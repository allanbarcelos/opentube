// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Access;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Access;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Playback;
using OpenTube.Infrastructure.Services;
using OpenTube.Infrastructure.Storage;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Services;

[Collection(IntegrationCollection.Name)]
public class CollectionThumbnailServiceTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();
    private static readonly SecurityOptions Seguranca = new() { TokenPepper = "segredo", IpHashPepper = "segredo" };

    private readonly FakeTimeProvider _relogio = new(Agora);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Visitante_ve_a_capa_de_colecao_publica_e_nao_a_de_colecao_privada()
    {
        using var storage = minio.CreateStorage();
        var publica = await PrepararAsync(storage, "Aberta", VideoVisibility.Public);
        var privada = await PrepararAsync(storage, "Fechada", VideoVisibility.Private);

        await using var db = postgres.CreateContext();
        var servico = Criar(db, storage);
        await servico.SetAsync(publica, PngDeTeste.Criar(400, 200));
        await servico.SetAsync(privada, PngDeTeste.Criar(400, 200));

        var daPublica = await servico.GetUrlAsync(publica, Viewer.Anonymous);
        var daPrivada = await servico.GetUrlAsync(privada, Viewer.Anonymous);
        var admin = Viewer.Authenticated(Admin, EmailAddress.Parse("admin@opentube.org"), isAdmin: true);

        Assert.NotNull(daPublica);
        Assert.Contains("X-Amz-Signature", daPublica);
        Assert.Null(daPrivada);
        Assert.NotNull(await servico.GetUrlAsync(privada, admin));
        Assert.Null(await servico.GetUrlAsync(Guid.CreateVersion7(), admin));

        var chavePublica = (await db.Collections.AsNoTracking().SingleAsync(c => c.Id == publica)).ThumbnailKey!;
        var chavePrivada = (await db.Collections.AsNoTracking().SingleAsync(c => c.Id == privada)).ThumbnailKey!;

        Assert.True(await servico.PodeEntregarAsync(chavePublica, Viewer.Anonymous));
        Assert.False(await servico.PodeEntregarAsync(chavePrivada, Viewer.Anonymous));
        Assert.True(await servico.PodeEntregarAsync(chavePrivada, admin));
        Assert.False(await servico.PodeEntregarAsync($"collections/{publica:n}/thumb-1.jpg", Viewer.Anonymous));
    }

    [Fact]
    public async Task Trocar_a_imagem_apaga_a_anterior_e_remover_volta_ao_padrao()
    {
        using var storage = minio.CreateStorage();
        var colecao = await PrepararAsync(storage, "Treinamentos", VideoVisibility.Public);

        await using var db = postgres.CreateContext();
        var servico = Criar(db, storage);

        await servico.SetAsync(colecao, PngDeTeste.Criar(400, 200));
        var primeira = await db.Collections.AsNoTracking().SingleAsync(c => c.Id == colecao);

        await servico.SetAsync(colecao, PngDeTeste.Criar(320, 180));
        var segunda = await db.Collections.AsNoTracking().SingleAsync(c => c.Id == colecao);

        Assert.NotEqual(primeira.ThumbnailKey, segunda.ThumbnailKey);
        Assert.True(segunda.ThumbnailVersion > primeira.ThumbnailVersion);
        Assert.False(await storage.ExistsAsync(StorageBucket.Vod, primeira.ThumbnailKey!));
        Assert.True(await storage.ExistsAsync(StorageBucket.Vod, segunda.ThumbnailKey!));

        Assert.True(await servico.RemoveAsync(colecao));

        var semCapa = await db.Collections.AsNoTracking().SingleAsync(c => c.Id == colecao);
        Assert.False(semCapa.HasThumbnail);
        Assert.False(await storage.ExistsAsync(StorageBucket.Vod, segunda.ThumbnailKey!));
        Assert.Null(await servico.GetUrlAsync(colecao, Viewer.Anonymous));
        Assert.False(await servico.RemoveAsync(colecao));
    }

    [Fact]
    public async Task Imagem_invalida_nao_grava_capa()
    {
        using var storage = minio.CreateStorage();
        var colecao = await PrepararAsync(storage, "Treinamentos", VideoVisibility.Public);

        await using var db = postgres.CreateContext();
        var servico = Criar(db, storage);

        await Assert.ThrowsAsync<ArgumentException>(() => servico.SetAsync(colecao, "texto"u8.ToArray()));

        var salva = await db.Collections.AsNoTracking().SingleAsync(c => c.Id == colecao);
        Assert.False(salva.HasThumbnail);
    }

    [Fact]
    public async Task Colecao_inexistente_nao_recebe_imagem()
    {
        using var storage = minio.CreateStorage();
        await using var db = postgres.CreateContext();
        var servico = Criar(db, storage);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servico.SetAsync(Guid.CreateVersion7(), PngDeTeste.Criar(400, 200)));
    }

    private CollectionThumbnailService Criar(OpenTubeDbContext db, IVideoStorage storage)
    {
        var acesso = new AccessService(db, Microsoft.Extensions.Options.Options.Create(Seguranca), _relogio);
        var catalogo = new VideoCatalog(db, acesso, _relogio);

        return new CollectionThumbnailService(
            db, storage, catalogo, _relogio, Microsoft.Extensions.Options.Options.Create(minio.Options), NullLogger<CollectionThumbnailService>.Instance);
    }

    private async Task<Guid> PrepararAsync(IVideoStorage storage, string nome, VideoVisibility visibilidade)
    {
        await using var db = postgres.CreateContext();
        var videoId = Guid.CreateVersion7();
        var video = Video.CreateDraft(nome, $"v-{videoId:n}", "originals/a.mp4", Admin, Agora, id: videoId);
        video.MarkUploaded(1024);
        video.StartProcessing();
        video.MarkReady(StorageKeys.VodPrefix(videoId), 60, 640, 360, null, null, Agora);
        if (visibilidade is not VideoVisibility.Private)
            video.ChangeVisibility(visibilidade);

        var colecao = Collection.Create(nome, $"c-{videoId:n}", Admin, Agora);
        colecao.Add(video.Id, Agora);

        db.Videos.Add(video);
        db.Collections.Add(colecao);
        await db.SaveChangesAsync();

        return colecao.Id;
    }
}
