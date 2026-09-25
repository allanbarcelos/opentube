using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Analytics;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Storage;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.Shared.Analytics;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Analytics;

[Collection(IntegrationCollection.Name)]
public class AnalyticsQueriesTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const double Duracao = 600;

    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();

    private readonly FakeTimeProvider _relogio = new(Agora);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private AnalyticsQueries Consultas(OpenTubeDbContext db) => new(db);

    private async Task<Video> CriarVideoAsync(string titulo = "Reunião")
    {
        await using var db = postgres.CreateContext();
        var videoId = Guid.CreateVersion7();

        var video = Video.CreateDraft(titulo, $"v-{videoId:n}"[..20], "originals/a.mp4", Admin, Agora, id: videoId);
        video.MarkUploaded(1024);
        video.StartProcessing();
        video.MarkReady(StorageKeys.VodPrefix(videoId), Duracao, 1280, 720, null, null, Agora);

        db.Videos.Add(video);
        await db.SaveChangesAsync();

        return video;
    }

    private async Task<Guid> CriarUsuarioAsync(string email)
    {
        await using var db = postgres.CreateContext();
        var usuario = User.Create(EmailAddress.Parse(email), Agora);

        db.Users.Add(usuario);
        await db.SaveChangesAsync();

        return usuario.Id;
    }

    private async Task AssistirAsync(
        Guid videoId, Guid? usuario, string? anonimo, double ate, bool concluiu = false, string? userAgent = null)
    {
        await using var db = postgres.CreateContext();
        var coletor = new AnalyticsCollector(db, _relogio, NullLogger<AnalyticsCollector>.Instance);

        var sessao = await coletor.StartAsync(videoId, usuario, anonimo, null, userAgent, null, null);

        var eventos = new List<PlaybackEventReport> { new("progress", ate, 0, ate) };
        if (concluiu)
            eventos.Add(new PlaybackEventReport("ended", ate));

        await coletor.RecordAsync(sessao.Id, usuario, anonimo, new PlaybackBatch([.. eventos]));
    }

    [Fact]
    public async Task Resume_a_audiencia_de_um_video()
    {
        var video = await CriarVideoAsync();
        var pessoa = await CriarUsuarioAsync("a@barcelos.dev");

        await AssistirAsync(video.Id, pessoa, null, 300);
        await AssistirAsync(video.Id, null, "visitante-1", Duracao, concluiu: true);

        await using var db = postgres.CreateContext();
        var resumo = await Consultas(db).VideoAudienceAsync(video.Id);

        Assert.Equal(2, resumo.Views);
        Assert.Equal(2, resumo.UniqueViewers);
        Assert.Equal(900, resumo.WatchSeconds);
        Assert.Equal(1, resumo.Completions);
        Assert.Equal(450, resumo.AverageWatchSeconds);
        Assert.Equal(0.5, resumo.CompletionRate);
        Assert.Equal(0.75, resumo.AverageCoverage, 3);
    }

    [Fact]
    public async Task Video_sem_audiencia_devolve_resumo_zerado()
    {
        var video = await CriarVideoAsync();

        await using var db = postgres.CreateContext();
        var resumo = await Consultas(db).VideoAudienceAsync(video.Id);

        Assert.Equal(0, resumo.Views);
        Assert.Equal(0, resumo.AverageWatchSeconds);
        Assert.Equal(Duracao, resumo.DurationSeconds);
    }

    [Fact]
    public async Task Lista_quem_assistiu_com_o_quanto_viu()
    {
        var video = await CriarVideoAsync();
        var pessoa = await CriarUsuarioAsync("allan@barcelos.dev");

        await AssistirAsync(video.Id, pessoa, null, 300);
        await AssistirAsync(video.Id, null, "visitante-1", 120);

        await using var db = postgres.CreateContext();
        var espectadores = await Consultas(db).ViewersAsync(video.Id);

        Assert.Equal(2, espectadores.Count);

        var identificado = espectadores.Single(e => e.Email == "allan@barcelos.dev");
        Assert.Equal(300, identificado.WatchSeconds);
        Assert.False(identificado.Completed);

        var anonimo = espectadores.Single(e => e.Email is null);
        Assert.Equal("Visitante não identificado", anonimo.DisplayName);
    }

    [Fact]
    public async Task A_mesma_pessoa_aparece_uma_vez_com_as_sessoes_somadas()
    {
        var video = await CriarVideoAsync();
        var pessoa = await CriarUsuarioAsync("allan@barcelos.dev");

        await AssistirAsync(video.Id, pessoa, null, 100);
        await AssistirAsync(video.Id, pessoa, null, 300);

        await using var db = postgres.CreateContext();
        var espectador = Assert.Single(await Consultas(db).ViewersAsync(video.Id));

        Assert.Equal(2, espectador.Sessions);
        Assert.Equal(300, espectador.WatchSeconds);
    }

    [Fact]
    public async Task Monta_a_linha_do_tempo_de_uma_pessoa()
    {
        var primeiro = await CriarVideoAsync("Primeiro");
        var segundo = await CriarVideoAsync("Segundo");
        var pessoa = await CriarUsuarioAsync("allan@barcelos.dev");

        await AssistirAsync(primeiro.Id, pessoa, null, 300);
        _relogio.Advance(TimeSpan.FromMinutes(10));
        await AssistirAsync(segundo.Id, pessoa, null, Duracao, concluiu: true);

        await using var db = postgres.CreateContext();
        var linha = await Consultas(db).ViewerTimelineAsync(pessoa);

        Assert.Equal(["Segundo", "Primeiro"], linha.Select(a => a.Title));
        Assert.True(linha[0].Completed);
        Assert.Equal(0.5, linha[1].Coverage, 3);
    }

    [Fact]
    public async Task A_linha_do_tempo_de_outra_pessoa_nao_se_mistura()
    {
        var video = await CriarVideoAsync();
        var allan = await CriarUsuarioAsync("allan@barcelos.dev");
        var outra = await CriarUsuarioAsync("outra@barcelos.dev");

        await AssistirAsync(video.Id, allan, null, 300);
        await AssistirAsync(video.Id, outra, null, 100);

        await using var db = postgres.CreateContext();

        Assert.Single(await Consultas(db).ViewerTimelineAsync(allan));
    }

    [Fact]
    public async Task Distribui_a_audiencia_por_aparelho_e_navegador()
    {
        var video = await CriarVideoAsync();

        await AssistirAsync(video.Id, null, "v1", 100,
            userAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1");
        await AssistirAsync(video.Id, null, "v2", 100,
            userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/140.0.0.0 Safari/537.36");

        await using var db = postgres.CreateContext();

        var aparelhos = await Consultas(db).BreakdownAsync(video.Id, "aparelho");
        Assert.Contains(aparelhos, a => a.Label == "Celular" && a.Count == 1);
        Assert.Contains(aparelhos, a => a.Label == "Computador" && a.Count == 1);

        var navegadores = await Consultas(db).BreakdownAsync(video.Id, "navegador");
        Assert.Contains(navegadores, n => n.Label == "Safari");
        Assert.Contains(navegadores, n => n.Label == "Chrome");
    }

    [Fact]
    public async Task A_curva_de_retencao_vem_em_percentual()
    {
        var video = await CriarVideoAsync();

        await AssistirAsync(video.Id, null, "v1", Duracao);
        await AssistirAsync(video.Id, null, "v2", 300);

        await using var db = postgres.CreateContext();
        await new AnalyticsAggregator(db, _relogio, NullLogger<AnalyticsAggregator>.Instance)
            .RebuildRetentionAsync(video.Id);

        var curva = await Consultas(db).RetentionAsync(video.Id);

        Assert.Equal(100, curva.Count);
        Assert.Equal(100, curva[0]);
        Assert.Equal(50, curva[99]);
    }

    [Fact]
    public async Task Video_sem_curva_devolve_lista_vazia()
    {
        var video = await CriarVideoAsync();

        await using var db = postgres.CreateContext();

        Assert.Empty(await Consultas(db).RetentionAsync(video.Id));
    }

    [Fact]
    public async Task A_serie_diaria_vem_dos_agregados()
    {
        var video = await CriarVideoAsync();
        await AssistirAsync(video.Id, null, "v1", 300);

        await using var db = postgres.CreateContext();
        await new AnalyticsAggregator(db, _relogio, NullLogger<AnalyticsAggregator>.Instance)
            .RollupDayAsync(new DateOnly(2026, 9, 24));

        var serie = await Consultas(db).DailySeriesAsync(video.Id, days: 3650);

        var ponto = Assert.Single(serie);
        Assert.Equal(1, ponto.Views);
        Assert.Equal(300, ponto.WatchSeconds);
    }

    [Fact]
    public async Task O_caminho_do_convite_mostra_quem_ainda_nao_abriu()
    {
        var video = await CriarVideoAsync();
        var assistiu = await CriarUsuarioAsync("assistiu@barcelos.dev");
        await CriarUsuarioAsync("entrou@barcelos.dev");

        await using (var db = postgres.CreateContext())
        {
            foreach (var email in new[] { "assistiu@barcelos.dev", "entrou@barcelos.dev", "nunca@barcelos.dev" })
            {
                db.AccessGrants.Add(AccessGrant.ForUser(
                    EmailAddress.Parse(email), GrantTargetType.Video, video.Id, Admin, Agora));
            }

            await db.SaveChangesAsync();
        }

        await AssistirAsync(video.Id, assistiu, null, Duracao, concluiu: true);

        await using var leitura = postgres.CreateContext();
        var caminho = await Consultas(leitura).InviteFunnelAsync(GrantTargetType.Video, video.Id);

        Assert.Equal(3, caminho.Invited);
        Assert.Equal(2, caminho.SignedIn);
        Assert.Equal(1, caminho.Watched);
        Assert.Equal(1, caminho.Completed);
        Assert.Equal(["entrou@barcelos.dev", "nunca@barcelos.dev"], caminho.Pending);
    }

    [Fact]
    public async Task Concessao_revogada_sai_do_caminho_do_convite()
    {
        var video = await CriarVideoAsync();

        await using (var db = postgres.CreateContext())
        {
            var concessao = AccessGrant.ForUser(
                EmailAddress.Parse("saiu@barcelos.dev"), GrantTargetType.Video, video.Id, Admin, Agora);
            concessao.Revoke(Agora);
            db.AccessGrants.Add(concessao);
            await db.SaveChangesAsync();
        }

        await using var leitura = postgres.CreateContext();
        var caminho = await Consultas(leitura).InviteFunnelAsync(GrantTargetType.Video, video.Id);

        Assert.Equal(0, caminho.Invited);
        Assert.Empty(caminho.Pending);
    }

    [Fact]
    public async Task O_caminho_de_uma_colecao_considera_os_videos_dela()
    {
        var video = await CriarVideoAsync();
        var pessoa = await CriarUsuarioAsync("allan@barcelos.dev");
        Guid colecaoId;

        await using (var db = postgres.CreateContext())
        {
            var colecao = Collection.Create("Treinamentos", "treinamentos", Admin, Agora);
            colecao.Add(video.Id);
            db.Collections.Add(colecao);

            db.AccessGrants.Add(AccessGrant.ForUser(
                EmailAddress.Parse("allan@barcelos.dev"), GrantTargetType.Collection, colecao.Id, Admin, Agora));

            await db.SaveChangesAsync();
            colecaoId = colecao.Id;
        }

        await AssistirAsync(video.Id, pessoa, null, 300);

        await using var leitura = postgres.CreateContext();
        var caminho = await Consultas(leitura).InviteFunnelAsync(GrantTargetType.Collection, colecaoId);

        Assert.Equal(1, caminho.Invited);
        Assert.Equal(1, caminho.Watched);
        Assert.Empty(caminho.Pending);
    }

    [Fact]
    public async Task Sem_convites_o_caminho_vem_zerado()
    {
        var video = await CriarVideoAsync();

        await using var db = postgres.CreateContext();
        var caminho = await Consultas(db).InviteFunnelAsync(GrantTargetType.Video, video.Id);

        Assert.Equal(0, caminho.Invited);
        Assert.Empty(caminho.Pending);
    }
}
