using OpenTube.Domain.Access;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Access;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Playback;
using OpenTube.Infrastructure.Storage;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Playback;

[Collection(IntegrationCollection.Name)]
public class PlaybackServiceTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static readonly Viewer Convidado =
        Viewer.Authenticated(Guid.CreateVersion7(), EmailAddress.Parse("allan@barcelos.dev"));

    private static readonly Viewer Administrador =
        Viewer.Authenticated(Guid.CreateVersion7(), EmailAddress.Parse("admin@opentube.org"), isAdmin: true);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly SecurityOptions Seguranca = new() { TokenPepper = "segredo", IpHashPepper = "segredo" };

    private PlaybackService Criar(OpenTube.Infrastructure.Persistence.OpenTubeDbContext db, IVideoStorage storage) =>
        new(db, storage,
            new AccessService(db, Microsoft.Extensions.Options.Options.Create(Seguranca), TimeProvider.System),
            Microsoft.Extensions.Options.Options.Create(minio.Options));

    private async Task<Video> PublicarAsync(IVideoStorage storage, VideoVisibility visibilidade)
    {
        var videoId = Guid.CreateVersion7();

        await storage.PutTextAsync(StorageBucket.Vod, StorageKeys.Master(videoId), """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=985000,RESOLUTION=640x360
            360p/stream.m3u8
            """, MediaTypes.HlsPlaylist);

        await storage.PutTextAsync(StorageBucket.Vod, StorageKeys.RenditionPlaylist(videoId, "360p"), """
            #EXTM3U
            #EXT-X-MAP:URI="init-360p.mp4"
            #EXTINF:4.0,
            seg-00000.m4s
            #EXT-X-ENDLIST
            """, MediaTypes.HlsPlaylist);

        await storage.PutTextAsync(StorageBucket.Vod, StorageKeys.Thumbnail(videoId), "jpeg-falso", MediaTypes.Jpeg);

        await using var db = postgres.CreateContext();
        var video = Video.CreateDraft("Amostra", $"amostra-{videoId:n}"[..30], "originals/a.mp4", Guid.CreateVersion7(), Agora, id: videoId);
        video.MarkUploaded(1024);
        video.StartProcessing();
        video.MarkReady(StorageKeys.VodPrefix(videoId), 120, 640, 360, StorageKeys.Thumbnail(videoId), StorageKeys.Sprite(videoId), Agora);
        if (visibilidade is not VideoVisibility.Private)
            video.ChangeVisibility(visibilidade);

        db.Videos.Add(video);
        await db.SaveChangesAsync();

        return video;
    }

    [Fact]
    public async Task Entrega_a_playlist_principal_de_um_video_publico()
    {
        using var storage = minio.CreateStorage();
        var video = await PublicarAsync(storage, VideoVisibility.Public);
        await using var db = postgres.CreateContext();

        var resultado = await Criar(db, storage).GetMasterAsync(video.Id, Viewer.Anonymous, v => $"/api/videos/{video.Id}/r/{v}.m3u8");

        Assert.True(resultado.Allowed);
        Assert.Equal(AccessReason.PublicVideo, resultado.Reason);
        Assert.Contains($"/api/videos/{video.Id}/r/360p.m3u8", resultado.Content);
        Assert.DoesNotContain("360p/stream.m3u8", resultado.Content);
    }

    [Fact]
    public async Task A_playlist_da_versao_vem_com_segmentos_assinados()
    {
        using var storage = minio.CreateStorage();
        var video = await PublicarAsync(storage, VideoVisibility.Public);
        await using var db = postgres.CreateContext();

        var resultado = await Criar(db, storage).GetRenditionAsync(video.Id, "360p", Viewer.Anonymous);

        Assert.True(resultado.Allowed);
        Assert.Contains("X-Amz-Signature", resultado.Content);
        Assert.Contains("seg-00000.m4s?", resultado.Content);
        // O arquivo de inicialização também precisa vir assinado, senão o player recebe
        // recusa logo no primeiro pedido.
        Assert.Contains("#EXT-X-MAP:URI=\"http", resultado.Content);
        Assert.Contains("init-360p.mp4?", resultado.Content);
    }

    [Fact]
    public async Task Os_segmentos_assinados_podem_ser_baixados()
    {
        using var storage = minio.CreateStorage();
        var video = await PublicarAsync(storage, VideoVisibility.Public);
        await storage.PutTextAsync(StorageBucket.Vod, $"{video.Id}/360p/seg-00000.m4s", "conteudo-do-segmento", MediaTypes.HlsSegment);

        await using var db = postgres.CreateContext();
        var resultado = await Criar(db, storage).GetRenditionAsync(video.Id, "360p", Viewer.Anonymous);

        var endereco = resultado.Content!
            .Split('\n')
            .First(l => l.Contains("seg-00000.m4s"))
            .Trim();

        using var http = new HttpClient();
        var resposta = await http.GetStringAsync(endereco);

        Assert.Equal("conteudo-do-segmento", resposta);
    }

    [Fact]
    public async Task Video_privado_e_negado_a_quem_nao_e_administrador()
    {
        using var storage = minio.CreateStorage();
        var video = await PublicarAsync(storage, VideoVisibility.Private);
        await using var db = postgres.CreateContext();

        var resultado = await Criar(db, storage).GetMasterAsync(video.Id, Convidado, v => v);

        Assert.False(resultado.Allowed);
        Assert.Equal(AccessReason.PrivateVideo, resultado.Reason);
        Assert.Null(resultado.Content);
    }

    [Fact]
    public async Task A_versao_tambem_e_autorizada_para_nao_contornar_a_playlist_principal()
    {
        using var storage = minio.CreateStorage();
        var video = await PublicarAsync(storage, VideoVisibility.Private);
        await using var db = postgres.CreateContext();

        // Copiar o endereço de uma versão não pode servir de atalho para o conteúdo.
        var resultado = await Criar(db, storage).GetRenditionAsync(video.Id, "360p", Convidado);

        Assert.False(resultado.Allowed);
        Assert.Null(resultado.Content);
    }

    [Fact]
    public async Task O_administrador_assiste_ao_video_privado()
    {
        using var storage = minio.CreateStorage();
        var video = await PublicarAsync(storage, VideoVisibility.Private);
        await using var db = postgres.CreateContext();

        var resultado = await Criar(db, storage).GetMasterAsync(video.Id, Administrador, v => v);

        Assert.True(resultado.Allowed);
        Assert.Equal(AccessReason.Administrator, resultado.Reason);
    }

    [Fact]
    public async Task Video_restrito_sem_concessao_e_negado()
    {
        using var storage = minio.CreateStorage();
        var video = await PublicarAsync(storage, VideoVisibility.Restricted);
        await using var db = postgres.CreateContext();

        var resultado = await Criar(db, storage).GetMasterAsync(video.Id, Convidado, v => v);

        Assert.False(resultado.Allowed);
        Assert.Equal(AccessReason.NoGrant, resultado.Reason);
    }

    [Fact]
    public async Task Video_inexistente_nao_revela_nada()
    {
        using var storage = minio.CreateStorage();
        await using var db = postgres.CreateContext();

        var resultado = await Criar(db, storage).GetMasterAsync(Guid.CreateVersion7(), Administrador, v => v);

        Assert.False(resultado.Allowed);
        Assert.Null(resultado.Content);
    }

    [Fact]
    public async Task A_miniatura_segue_a_mesma_regra_de_acesso()
    {
        using var storage = minio.CreateStorage();
        var publico = await PublicarAsync(storage, VideoVisibility.Public);
        var privado = await PublicarAsync(storage, VideoVisibility.Private);
        await using var db = postgres.CreateContext();
        var servico = Criar(db, storage);

        Assert.NotNull(await servico.GetThumbnailUrlAsync(publico.Id, Viewer.Anonymous));
        Assert.Null(await servico.GetThumbnailUrlAsync(privado.Id, Viewer.Anonymous));
        Assert.NotNull(await servico.GetThumbnailUrlAsync(privado.Id, Administrador));
    }
}
