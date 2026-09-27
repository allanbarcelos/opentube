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
using OpenTube.Infrastructure.Services;
using OpenTube.Infrastructure.Storage;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Services;

/// <summary>Avaliação de utilidade: uma nota por pessoa, só de quem pode assistir, e o conjunto para a administração.</summary>
[Collection(IntegrationCollection.Name)]
public class VideoRatingServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _relogio = new(Agora);

    private readonly SecurityOptions _seguranca = new()
    {
        TokenPepper = "segredo",
        IpHashPepper = "segredo-ip",
        PublicUrl = "https://opentube.org"
    };

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private VideoRatingService Criar(OpenTubeDbContext db) =>
        new(db, new AccessService(db, Microsoft.Extensions.Options.Options.Create(_seguranca), _relogio), _relogio);

    private async Task<Video> VideoAsync(VideoVisibility visibilidade = VideoVisibility.Public)
    {
        await using var db = postgres.CreateContext();
        var id = Guid.CreateVersion7();
        var video = Video.CreateDraft("Reunião", $"v-{id:n}"[..20], StorageKeys.Original(id, "a.mp4"), Guid.CreateVersion7(), Agora, id: id);
        video.MarkUploaded(1024);
        video.StartProcessing();
        video.MarkReady(StorageKeys.VodPrefix(id), 600, 1280, 720, null, null, Agora);
        if (visibilidade is not VideoVisibility.Private)
            video.ChangeVisibility(visibilidade);
        db.Videos.Add(video);
        await db.SaveChangesAsync();
        return video;
    }

    private async Task<Viewer> PessoaAsync(string email)
    {
        await using var db = postgres.CreateContext();
        var usuario = User.Create(EmailAddress.Parse(email), Agora);
        db.Users.Add(usuario);
        await db.SaveChangesAsync();
        return Viewer.From(usuario);
    }

    [Fact]
    public async Task Uma_nota_por_pessoa_que_ela_pode_trocar()
    {
        var video = await VideoAsync();
        var pessoa = await PessoaAsync("ana@barcelos.dev");

        await using (var db = postgres.CreateContext())
        {
            await Criar(db).RateAsync(video.Id, pessoa, 2);
            await Criar(db).RateAsync(video.Id, pessoa, 5);
            Assert.Equal(5, await Criar(db).GetMineAsync(video.Id, pessoa));
        }

        await using var leitura = postgres.CreateContext();
        var nota = await leitura.VideoRatings.SingleAsync();
        Assert.Equal(5, nota.Score);
    }

    [Fact]
    public async Task A_administracao_ve_media_total_e_distribuicao()
    {
        var video = await VideoAsync();
        var notas = new[] { 5, 5, 4, 2 };

        for (var i = 0; i < notas.Length; i++)
        {
            var pessoa = await PessoaAsync($"pessoa{i}@barcelos.dev");
            await using var db = postgres.CreateContext();
            await Criar(db).RateAsync(video.Id, pessoa, notas[i]);
        }

        await using var leitura = postgres.CreateContext();
        var resumo = await Criar(leitura).SummaryAsync(video.Id);

        Assert.Equal(4, resumo.Count);
        Assert.Equal(4.0, resumo.Average);
        Assert.Equal([0, 1, 0, 1, 2], resumo.Distribution);

        Assert.Equal(RatingSummary.Empty, await Criar(leitura).SummaryAsync((await VideoAsync()).Id));
    }

    [Fact]
    public async Task Quem_nao_entrou_nao_avalia()
    {
        var video = await VideoAsync();

        await using var db = postgres.CreateContext();
        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() => Criar(db).RateAsync(video.Id, Viewer.Anonymous, 4));

        Assert.Equal("Sign in to rate this video.", erro.Message);
        Assert.Null(await Criar(db).GetMineAsync(video.Id, Viewer.Anonymous));
    }

    [Fact]
    public async Task Quem_nao_pode_assistir_nao_avalia()
    {
        var privado = await VideoAsync(VideoVisibility.Private);
        var pessoa = await PessoaAsync("ana@barcelos.dev");

        await using var db = postgres.CreateContext();
        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() => Criar(db).RateAsync(privado.Id, pessoa, 4));

        Assert.Equal("Video not found", erro.Message);
        Assert.Equal(0, await db.VideoRatings.CountAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task Nota_fora_da_escala_e_recusada(int nota)
    {
        var video = await VideoAsync();
        var pessoa = await PessoaAsync("ana@barcelos.dev");

        await using var db = postgres.CreateContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Criar(db).RateAsync(video.Id, pessoa, nota));
    }
}
