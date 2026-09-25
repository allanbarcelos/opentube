using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Entities;
using OpenTube.Infrastructure.Analytics;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Playback;
using OpenTube.Infrastructure.Storage;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Playback;

/// <summary>
/// Limite de reproduções simultâneas. Ele existe para detectar credencial repassada, e por
/// isso conta origens distintas, não sessões.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PlaybackGuardTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Pessoa = Guid.CreateVersion7();

    private readonly FakeTimeProvider _relogio = new(Agora);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private PlaybackGuard Criar(OpenTubeDbContext db, int limite) =>
        new(db, Microsoft.Extensions.Options.Options.Create(new SecurityOptions
        {
            TokenPepper = "x",
            IpHashPepper = "y",
            MaxConcurrentPlaybacks = limite
        }), _relogio);

    private async Task<Video> CriarVideoAsync()
    {
        await using var db = postgres.CreateContext();
        var videoId = Guid.CreateVersion7();

        var video = Video.CreateDraft("Reunião", $"v-{videoId:n}"[..20], "originals/a.mp4", Guid.CreateVersion7(), Agora, id: videoId);
        video.MarkUploaded(1024);
        video.StartProcessing();
        video.MarkReady(StorageKeys.VodPrefix(videoId), 600, 1280, 720, null, null, Agora);

        db.Videos.Add(video);
        await db.SaveChangesAsync();

        return video;
    }

    private async Task AbrirSessaoAsync(Guid videoId, string? ipHash, Guid? usuario = null)
    {
        await using var db = postgres.CreateContext();
        var coletor = new AnalyticsCollector(db, _relogio, NullLogger<AnalyticsCollector>.Instance);

        await coletor.StartAsync(videoId, usuario ?? Pessoa, null, null, null, ipHash, null);
    }

    [Fact]
    public async Task Sem_nenhuma_reproducao_ativa_o_acesso_e_liberado()
    {
        await using var db = postgres.CreateContext();

        Assert.True(await Criar(db, 2).AllowsAnotherAsync(Pessoa, "origem-1"));
    }

    [Fact]
    public async Task Recarregar_a_pagina_nao_consome_o_limite()
    {
        var video = await CriarVideoAsync();
        await AbrirSessaoAsync(video.Id, "origem-1");
        await AbrirSessaoAsync(video.Id, "origem-1");
        await AbrirSessaoAsync(video.Id, "origem-1");

        await using var db = postgres.CreateContext();

        // Três sessões, uma origem só: é a mesma pessoa no mesmo lugar.
        Assert.True(await Criar(db, 2).AllowsAnotherAsync(Pessoa, "origem-1"));
    }

    [Fact]
    public async Task Origens_distintas_consomem_o_limite()
    {
        var video = await CriarVideoAsync();
        await AbrirSessaoAsync(video.Id, "origem-1");
        await AbrirSessaoAsync(video.Id, "origem-2");

        await using var db = postgres.CreateContext();
        var limite = Criar(db, 2);

        Assert.False(await limite.AllowsAnotherAsync(Pessoa, "origem-3"));
        // Quem já está entre as origens contadas continua passando.
        Assert.True(await limite.AllowsAnotherAsync(Pessoa, "origem-1"));
    }

    [Fact]
    public async Task Sessao_parada_deixa_de_contar()
    {
        var video = await CriarVideoAsync();
        await AbrirSessaoAsync(video.Id, "origem-1");
        await AbrirSessaoAsync(video.Id, "origem-2");

        _relogio.Advance(AnalyticsCollector.InactivityTimeout + TimeSpan.FromMinutes(1));

        await using var db = postgres.CreateContext();

        Assert.True(await Criar(db, 2).AllowsAnotherAsync(Pessoa, "origem-3"));
    }

    [Fact]
    public async Task Sessao_encerrada_deixa_de_contar()
    {
        var video = await CriarVideoAsync();
        await AbrirSessaoAsync(video.Id, "origem-1");
        await AbrirSessaoAsync(video.Id, "origem-2");

        await using (var db = postgres.CreateContext())
        {
            var coletor = new AnalyticsCollector(db, _relogio, NullLogger<AnalyticsCollector>.Instance);
            foreach (var sessao in db.PlaybackSessions.ToList())
                await coletor.EndAsync(sessao.Id, Pessoa, null);
        }

        await using var leitura = postgres.CreateContext();

        Assert.True(await Criar(leitura, 2).AllowsAnotherAsync(Pessoa, "origem-3"));
    }

    [Fact]
    public async Task A_reproducao_de_outra_pessoa_nao_entra_na_conta()
    {
        var video = await CriarVideoAsync();
        var outra = Guid.CreateVersion7();

        await AbrirSessaoAsync(video.Id, "origem-1", outra);
        await AbrirSessaoAsync(video.Id, "origem-2", outra);

        await using var db = postgres.CreateContext();

        Assert.True(await Criar(db, 2).AllowsAnotherAsync(Pessoa, "origem-3"));
    }

    [Fact]
    public async Task Origem_desconhecida_nao_e_contada()
    {
        var video = await CriarVideoAsync();
        await AbrirSessaoAsync(video.Id, null);
        await AbrirSessaoAsync(video.Id, null);
        await AbrirSessaoAsync(video.Id, null);

        await using var db = postgres.CreateContext();

        // Chutar que cada requisição sem origem conhecida é alguém novo puniria quem está
        // atrás de um proxy.
        Assert.True(await Criar(db, 1).AllowsAnotherAsync(Pessoa, null));
    }

    [Fact]
    public async Task Limite_zerado_desliga_a_verificacao()
    {
        var video = await CriarVideoAsync();
        await AbrirSessaoAsync(video.Id, "origem-1");
        await AbrirSessaoAsync(video.Id, "origem-2");
        await AbrirSessaoAsync(video.Id, "origem-3");

        await using var db = postgres.CreateContext();

        Assert.True(await Criar(db, 0).AllowsAnotherAsync(Pessoa, "origem-4"));
    }

    [Fact]
    public async Task Visitante_anonimo_nao_e_limitado()
    {
        await using var db = postgres.CreateContext();

        Assert.True(await Criar(db, 1).AllowsAnotherAsync(null, "origem-1"));
    }
}
