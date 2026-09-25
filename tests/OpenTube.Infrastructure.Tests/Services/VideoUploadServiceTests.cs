using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Queue;
using OpenTube.Infrastructure.Services;
using OpenTube.Infrastructure.Storage;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Services;

[Collection(IntegrationCollection.Name)]
public class VideoUploadServiceTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();
    private static readonly HttpClient Http = new();

    private readonly FakeTimeProvider _relogio = new(Agora);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private (VideoUploadService Servico, OpenTubeDbContext Db, S3VideoStorage Storage, IJobQueue Fila) Criar()
    {
        var db = postgres.CreateContext();
        var storage = minio.CreateStorage();
        var fila = new PostgresJobQueue(db, _relogio);
        var servico = new VideoUploadService(
            db, storage, fila, Microsoft.Extensions.Options.Options.Create(minio.Options), _relogio, NullLogger<VideoUploadService>.Instance);

        return (servico, db, storage, fila);
    }

    private static async Task<CompletedPart> EnviarPedacoAsync(UploadPartUrl parte, byte[] dados)
    {
        var conteudo = new ByteArrayContent(dados);
        conteudo.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var resposta = await Http.PutAsync(parte.Url, conteudo);
        resposta.EnsureSuccessStatusCode();

        return new CompletedPart(parte.PartNumber, resposta.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Cria_rascunho_privado_e_assina_os_pedacos()
    {
        var (servico, db, storage, _) = Criar();
        using var _1 = storage;
        await using var _2 = db;

        var bilhete = await servico.StartAsync("Reunião Trimestral", "Resultados", "reuniao.mp4", "video/mp4", 3 * 1024 * 1024, Admin);

        Assert.Equal(1, bilhete.PartCount);
        Assert.Single(bilhete.Parts);
        Assert.False(string.IsNullOrWhiteSpace(bilhete.UploadId));
        Assert.Equal($"{bilhete.VideoId}/source.mp4", bilhete.StorageKey);

        var video = await db.Videos.SingleAsync();
        Assert.Equal(VideoStatus.Draft, video.Status);
        Assert.Equal(VideoVisibility.Private, video.Visibility);
        Assert.Equal("reuniao-trimestral", video.Slug);
        Assert.Equal(Admin, video.CreatedBy);
    }

    [Fact]
    public async Task Gera_endereco_unico_quando_o_titulo_se_repete()
    {
        var (servico, db, storage, _) = Criar();
        using var _1 = storage;
        await using var _2 = db;

        await servico.StartAsync("Reunião", null, "a.mp4", "video/mp4", 1024, Admin);
        await servico.StartAsync("Reunião", null, "b.mp4", "video/mp4", 1024, Admin);
        await servico.StartAsync("Reunião", null, "c.mp4", "video/mp4", 1024, Admin);

        var enderecos = await db.Videos.Select(v => v.Slug).OrderBy(s => s).ToListAsync();

        Assert.Equal(["reuniao", "reuniao-2", "reuniao-3"], enderecos);
    }

    [Fact]
    public async Task Assina_apenas_o_primeiro_lote_de_pedacos()
    {
        var (servico, db, storage, _) = Criar();
        using var _1 = storage;
        await using var _2 = db;

        // 1 GiB em pedaços de 5 MiB dá 205 pedaços; só o primeiro lote é assinado de imediato.
        var bilhete = await servico.StartAsync("Longo", null, "longo.mp4", "video/mp4", 1024L * 1024 * 1024, Admin);

        Assert.Equal(205, bilhete.PartCount);
        Assert.Equal(VideoUploadService.PartUrlBatchSize, bilhete.Parts.Count);

        var proximos = await servico.SignMorePartsAsync(bilhete.VideoId, bilhete.UploadId, 51, 50);

        Assert.Equal(51, proximos[0].PartNumber);
        Assert.Equal(100, proximos[^1].PartNumber);
    }

    [Fact]
    public async Task Recusa_arquivo_que_nao_e_video()
    {
        var (servico, db, storage, _) = Criar();
        using var _1 = storage;
        await using var _2 = db;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servico.StartAsync("Planilha", null, "orcamento.xlsx", "application/vnd.ms-excel", 1024, Admin));

        Assert.Empty(await db.Videos.ToListAsync());
    }

    [Fact]
    public async Task Recusa_arquivo_acima_do_limite_configurado()
    {
        var (servico, db, storage, _) = Criar();
        using var _1 = storage;
        await using var _2 = db;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servico.StartAsync("Enorme", null, "enorme.mp4", "video/mp4", minio.Options.MaxUploadBytes + 1, Admin));
    }

    [Theory]
    [InlineData("", "a.mp4", 1024)]
    [InlineData("Título", "", 1024)]
    [InlineData("Título", "a.mp4", 0)]
    public async Task Recusa_parametros_invalidos(string titulo, string arquivo, long tamanho)
    {
        var (servico, db, storage, _) = Criar();
        using var _1 = storage;
        await using var _2 = db;

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            servico.StartAsync(titulo, null, arquivo, "video/mp4", tamanho, Admin));
    }

    [Fact]
    public async Task Concluir_o_envio_marca_o_video_e_enfileira_a_transcodificacao()
    {
        var (servico, db, storage, fila) = Criar();
        using var _1 = storage;
        await using var _2 = db;

        var dados = new byte[1024];
        Random.Shared.NextBytes(dados);

        var bilhete = await servico.StartAsync("Reunião", null, "reuniao.mp4", "video/mp4", dados.Length, Admin);
        var enviado = await EnviarPedacoAsync(bilhete.Parts[0], dados);

        var video = await servico.CompleteAsync(bilhete.VideoId, bilhete.UploadId, [enviado]);

        Assert.Equal(VideoStatus.Uploaded, video.Status);
        Assert.Equal(dados.Length, video.SizeBytes);
        Assert.True(await storage.ExistsAsync(StorageBucket.Originals, bilhete.StorageKey));

        var job = await fila.DequeueAsync("worker-1", [JobKind.Transcode], TimeSpan.FromMinutes(5));
        Assert.NotNull(job);
        Assert.Equal(bilhete.VideoId, job.TargetId);

        var payload = job.PayloadAs<TranscodePayload>();
        Assert.Equal(bilhete.StorageKey, payload!.OriginalKey);
    }

    [Fact]
    public async Task Nao_permite_concluir_duas_vezes()
    {
        var (servico, db, storage, _) = Criar();
        using var _1 = storage;
        await using var _2 = db;

        var dados = new byte[512];
        var bilhete = await servico.StartAsync("Reunião", null, "reuniao.mp4", "video/mp4", dados.Length, Admin);
        var enviado = await EnviarPedacoAsync(bilhete.Parts[0], dados);
        await servico.CompleteAsync(bilhete.VideoId, bilhete.UploadId, [enviado]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servico.CompleteAsync(bilhete.VideoId, bilhete.UploadId, [enviado]));
    }

    [Fact]
    public async Task Cancelar_apaga_o_rascunho_e_libera_os_pedacos()
    {
        var (servico, db, storage, _) = Criar();
        using var _1 = storage;
        await using var _2 = db;

        var bilhete = await servico.StartAsync("Abandonado", null, "a.mp4", "video/mp4", 1024, Admin);

        await servico.AbortAsync(bilhete.VideoId, bilhete.UploadId);

        Assert.Empty(await db.Videos.ToListAsync());
        Assert.False(await storage.ExistsAsync(StorageBucket.Originals, bilhete.StorageKey));
    }

    [Fact]
    public async Task Recusa_operar_sobre_video_inexistente()
    {
        var (servico, db, storage, _) = Criar();
        using var _1 = storage;
        await using var _2 = db;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servico.CompleteAsync(Guid.CreateVersion7(), "upload", []));
    }
}
