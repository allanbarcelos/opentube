using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Queue;
using OpenTube.Infrastructure.Storage;
using OpenTube.TestSupport;
using OpenTube.Worker.Jobs;
using OpenTube.Worker.Tests.Support;

namespace OpenTube.Worker.Tests.Jobs;

/// <summary>
/// Geração de legenda a partir da fala, com o texto alimentando a busca.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class TranscriptionJobHandlerTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();

    private readonly FakeTimeProvider _relogio = new(Agora);
    private readonly FakeTranscriber _transcritor = new();

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TranscriptionJobHandler Criar(OpenTube.Infrastructure.Persistence.OpenTubeDbContext db, IVideoStorage storage) =>
        new(db, storage, _transcritor, _relogio, NullLogger<TranscriptionJobHandler>.Instance);

    private async Task<Video> PrepararVideoAsync(IVideoStorage storage)
    {
        var videoId = Guid.CreateVersion7();
        var chave = StorageKeys.Original(videoId, "amostra.mp4");

        await storage.PutTextAsync(StorageBucket.Originals, chave, "arquivo-falso", MediaTypes.Mp4);

        await using var db = postgres.CreateContext();

        var video = Video.CreateDraft("Reunião", $"v-{videoId:n}"[..20], chave, Admin, Agora, id: videoId);
        video.MarkUploaded(1024);
        video.StartProcessing();
        video.MarkReady(StorageKeys.VodPrefix(videoId), 600, 1280, 720, null, null, Agora);

        db.Videos.Add(video);
        await db.SaveChangesAsync();

        return video;
    }

    private static QueuedJob Job(Video video) =>
        new(Guid.CreateVersion7(), JobKind.Transcript, video.Id,
            System.Text.Json.JsonSerializer.Serialize(new TranscriptionPayload(video.Id, video.OriginalKey)), 1);

    [Fact]
    public async Task Gera_a_legenda_e_alimenta_a_busca()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video));

        await using var leitura = postgres.CreateContext();
        var gravado = await leitura.Videos.SingleAsync(v => v.Id == video.Id);
        var legenda = await leitura.VideoAssets.SingleAsync(a => a.Kind == VideoAssetKind.Caption);

        Assert.Equal("Bom dia a todos.", gravado.Transcript);
        Assert.Equal("pt", legenda.Language);
        Assert.Equal(StorageKeys.Caption(video.Id, "pt"), legenda.StorageKey);
        Assert.True(await storage.ExistsAsync(StorageBucket.Vod, legenda.StorageKey));
    }

    [Fact]
    public async Task A_fala_transcrita_passa_a_ser_encontrada_na_busca()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);

        _transcritor.Vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:04.000\nVamos falar do orçamento anual.\n";

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video));

        await using var leitura = postgres.CreateContext();
        var conexao = leitura.Database.GetDbConnection();

        var achou = await Dapper.SqlMapper.ExecuteScalarAsync<bool>(conexao,
            "SELECT search_vector @@ plainto_tsquery('portuguese_unaccent', 'orcamento') FROM videos WHERE id = @Id",
            new { Id = video.Id });

        Assert.True(achou, "a fala transcrita deveria entrar no índice de busca");
    }

    [Fact]
    public async Task Transcrever_de_novo_substitui_a_legenda_anterior()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video));

        _transcritor.Vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:04.000\nTexto corrigido.\n";

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video));

        await using var leitura = postgres.CreateContext();

        Assert.Equal(1, await leitura.VideoAssets.CountAsync(a => a.Kind == VideoAssetKind.Caption));
        Assert.Equal("Texto corrigido.", (await leitura.Videos.SingleAsync()).Transcript);
    }

    [Fact]
    public async Task Sem_a_ferramenta_configurada_o_trabalho_e_descartado()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);
        _transcritor.IsAvailable = false;

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video));

        Assert.Equal(0, _transcritor.Chamadas);

        await using var leitura = postgres.CreateContext();
        Assert.Empty(await leitura.VideoAssets.ToListAsync());
    }

    [Fact]
    public async Task Video_excluido_nao_e_transcrito()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);

        await using (var db = postgres.CreateContext())
        {
            var alvo = await db.Videos.SingleAsync(v => v.Id == video.Id);
            alvo.SoftDelete(Agora);
            await db.SaveChangesAsync();
        }

        await using (var db = postgres.CreateContext())
            await Criar(db, storage).HandleAsync(Job(video));

        Assert.Equal(0, _transcritor.Chamadas);
    }

    [Fact]
    public async Task A_falha_da_ferramenta_propaga_para_a_fila()
    {
        using var storage = minio.CreateStorage();
        var video = await PrepararVideoAsync(storage);
        _transcritor.Falha = new InvalidOperationException("modelo não encontrado");

        await using var db = postgres.CreateContext();

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() => Criar(db, storage).HandleAsync(Job(video)));

        Assert.Contains("modelo não encontrado", erro.Message);
    }

    [Fact]
    public async Task Recusa_trabalho_sem_parametros()
    {
        using var storage = minio.CreateStorage();
        await using var db = postgres.CreateContext();

        var job = new QueuedJob(Guid.CreateVersion7(), JobKind.Transcript, null, "{}", 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Criar(db, storage).HandleAsync(job));
    }
}
