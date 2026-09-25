using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Queue;
using OpenTube.Infrastructure.Services;
using OpenTube.Infrastructure.Storage;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Services;

[Collection(IntegrationCollection.Name)]
public class AdminVideoServiceTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();

    private readonly FakeTimeProvider _relogio = new(Agora);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private (AdminVideoService Servico, OpenTubeDbContext Db, IJobQueue Fila, IVideoStorage Storage) Criar()
    {
        var db = postgres.CreateContext();
        var storage = minio.CreateStorage();
        var fila = new PostgresJobQueue(db, _relogio);

        return (new AdminVideoService(db, fila, storage, _relogio, NullLogger<AdminVideoService>.Instance), db, fila, storage);
    }

    private async Task<Video> CriarVideoAsync(bool pronto = true, IVideoStorage? storage = null)
    {
        var videoId = Guid.CreateVersion7();
        var chave = StorageKeys.Original(videoId, "amostra.mp4");

        if (storage is not null)
            await storage.PutTextAsync(StorageBucket.Originals, chave, "arquivo-falso", MediaTypes.Mp4);

        await using var db = postgres.CreateContext();

        var video = Video.CreateDraft("Reunião", $"reuniao-{videoId:n}"[..30], chave, Admin, Agora, id: videoId);
        video.MarkUploaded(2048);

        if (pronto)
        {
            video.StartProcessing();
            video.MarkReady(StorageKeys.VodPrefix(videoId), 120, 1280, 720, null, null, Agora);
        }

        db.Videos.Add(video);
        await db.SaveChangesAsync();

        return video;
    }

    [Fact]
    public async Task Atualiza_titulo_descricao_etiquetas_e_visibilidade()
    {
        var video = await CriarVideoAsync();
        var (servico, db, _, storage) = Criar();
        using var _1 = (IDisposable)storage;
        await using var _2 = db;

        await servico.UpdateAsync(video.Id, "Reunião Trimestral", "Resultados", ["financeiro", "Q3"], VideoVisibility.Public);

        await using var leitura = postgres.CreateContext();
        var atualizado = await leitura.Videos.SingleAsync(v => v.Id == video.Id);

        Assert.Equal("Reunião Trimestral", atualizado.Title);
        Assert.Equal("Resultados", atualizado.Description);
        Assert.Equal(["financeiro", "q3"], atualizado.Tags);
        Assert.Equal(VideoVisibility.Public, atualizado.Visibility);
    }

    [Fact]
    public async Task Mudar_o_titulo_nao_quebra_o_link_ja_compartilhado()
    {
        var video = await CriarVideoAsync();
        var enderecoOriginal = video.Slug;
        var (servico, db, _, storage) = Criar();
        using var _1 = (IDisposable)storage;
        await using var _2 = db;

        await servico.UpdateAsync(video.Id, "Título completamente diferente", null, null, VideoVisibility.Private);

        await using var leitura = postgres.CreateContext();
        Assert.Equal(enderecoOriginal, (await leitura.Videos.SingleAsync(v => v.Id == video.Id)).Slug);
    }

    [Fact]
    public async Task Nao_libera_video_que_ainda_nao_esta_pronto()
    {
        var video = await CriarVideoAsync(pronto: false);
        var (servico, db, _, storage) = Criar();
        using var _1 = (IDisposable)storage;
        await using var _2 = db;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servico.UpdateAsync(video.Id, "Título", null, null, VideoVisibility.Public));
    }

    [Fact]
    public async Task Altera_apenas_a_visibilidade()
    {
        var video = await CriarVideoAsync();
        var (servico, db, _, storage) = Criar();
        using var _1 = (IDisposable)storage;
        await using var _2 = db;

        await servico.ChangeVisibilityAsync(video.Id, VideoVisibility.Restricted);

        await using var leitura = postgres.CreateContext();
        var atualizado = await leitura.Videos.SingleAsync(v => v.Id == video.Id);

        Assert.Equal(VideoVisibility.Restricted, atualizado.Visibility);
        Assert.Equal("Reunião", atualizado.Title);
    }

    [Fact]
    public async Task Excluir_tira_do_ar_e_volta_o_video_para_privado()
    {
        var video = await CriarVideoAsync();
        var (servico, db, _, storage) = Criar();
        using var _1 = (IDisposable)storage;
        await using var _2 = db;
        await servico.ChangeVisibilityAsync(video.Id, VideoVisibility.Public);

        await servico.DeleteAsync(video.Id);

        await using var leitura = postgres.CreateContext();
        var excluido = await leitura.Videos.SingleAsync(v => v.Id == video.Id);

        Assert.True(excluido.IsDeleted);
        Assert.Equal(VideoVisibility.Private, excluido.Visibility);
        Assert.Equal(Agora, excluido.DeletedAt);
    }

    [Fact]
    public async Task O_registro_sobrevive_a_exclusao()
    {
        var video = await CriarVideoAsync();
        var (servico, db, _, storage) = Criar();
        using var _1 = (IDisposable)storage;
        await using var _2 = db;

        await servico.DeleteAsync(video.Id);

        await using var leitura = postgres.CreateContext();
        // Apagar a linha destruiria o histórico de quem assistiu antes da exclusão.
        Assert.Equal(1, await leitura.Videos.CountAsync());
    }

    [Fact]
    public async Task Restaurar_devolve_o_video_ainda_privado()
    {
        var video = await CriarVideoAsync();
        var (servico, db, _, storage) = Criar();
        using var _1 = (IDisposable)storage;
        await using var _2 = db;
        await servico.DeleteAsync(video.Id);

        await servico.RestoreAsync(video.Id);

        await using var leitura = postgres.CreateContext();
        var restaurado = await leitura.Videos.SingleAsync(v => v.Id == video.Id);

        Assert.False(restaurado.IsDeleted);
        Assert.Equal(VideoVisibility.Private, restaurado.Visibility);
    }

    [Fact]
    public async Task Reprocessar_enfileira_a_partir_do_original()
    {
        using var storage = minio.CreateStorage();
        var video = await CriarVideoAsync(storage: storage);
        var (servico, db, fila, _) = Criar();
        await using var _2 = db;

        await servico.RequeueAsync(video.Id);

        var job = await fila.DequeueAsync("worker-1", [JobKind.Transcode], TimeSpan.FromMinutes(5));

        Assert.NotNull(job);
        Assert.Equal(video.Id, job.TargetId);
        Assert.Equal(video.OriginalKey, job.PayloadAs<TranscodePayload>()!.OriginalKey);
    }

    [Fact]
    public async Task Nao_enfileira_outra_transcodificacao_enquanto_uma_esta_aberta()
    {
        using var storage = minio.CreateStorage();
        var video = await CriarVideoAsync(storage: storage);
        var (servico, db, _, _) = Criar();
        await using var _2 = db;

        await servico.RequeueAsync(video.Id);

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() => servico.RequeueAsync(video.Id));

        Assert.Contains("já está na fila", erro.Message);
    }

    [Fact]
    public async Task Nao_reprocessa_sem_o_arquivo_original()
    {
        var video = await CriarVideoAsync();
        var (servico, db, _, storage) = Criar();
        using var _1 = (IDisposable)storage;
        await using var _2 = db;

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() => servico.RequeueAsync(video.Id));

        Assert.Contains("não está mais no storage", erro.Message);
    }

    [Fact]
    public async Task Nao_reprocessa_rascunho_sem_arquivo()
    {
        var videoId = Guid.CreateVersion7();

        await using (var db = postgres.CreateContext())
        {
            db.Videos.Add(Video.CreateDraft("Rascunho", "rascunho", "chave", Admin, Agora, id: videoId));
            await db.SaveChangesAsync();
        }

        var (servico, db2, _, storage) = Criar();
        using var _1 = (IDisposable)storage;
        await using var _2 = db2;

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() => servico.RequeueAsync(videoId));

        Assert.Contains("ainda não terminou de ser enviado", erro.Message);
    }

    [Fact]
    public async Task Resume_a_fila_por_estado()
    {
        using var storage = minio.CreateStorage();
        var video = await CriarVideoAsync(storage: storage);
        var (servico, db, _, _) = Criar();
        await using var _2 = db;

        await servico.RequeueAsync(video.Id);

        var resumo = await servico.QueueSummaryAsync();

        Assert.Equal(1, resumo[JobStatus.Pending]);
    }

    [Fact]
    public async Task Recusa_operar_sobre_video_inexistente()
    {
        var (servico, db, _, storage) = Criar();
        using var _1 = (IDisposable)storage;
        await using var _2 = db;
        var inexistente = Guid.CreateVersion7();

        await Assert.ThrowsAsync<InvalidOperationException>(() => servico.DeleteAsync(inexistente));
        await Assert.ThrowsAsync<InvalidOperationException>(() => servico.RequeueAsync(inexistente));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servico.UpdateAsync(inexistente, "x", null, null, VideoVisibility.Private));
    }

    [Fact]
    public async Task Encontra_o_video_pelo_identificador()
    {
        var video = await CriarVideoAsync();
        var (servico, db, _, storage) = Criar();
        using var _1 = (IDisposable)storage;
        await using var _2 = db;

        Assert.NotNull(await servico.FindAsync(video.Id));
        Assert.Null(await servico.FindAsync(Guid.CreateVersion7()));
    }
}
