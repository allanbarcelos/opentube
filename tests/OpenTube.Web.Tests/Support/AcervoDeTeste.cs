using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Storage;
using OpenTube.TestSupport;

namespace OpenTube.Web.Tests.Support;

/// <summary>Monta vídeos prontos para reprodução, com as saídas já no storage.</summary>
public static class AcervoDeTeste
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    public static async Task<Video> PublicarAsync(
        PostgresFixture postgres,
        IVideoStorage storage,
        string titulo,
        VideoVisibility visibilidade,
        string? descricao = null,
        IEnumerable<string>? etiquetas = null)
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

        var video = Video.CreateDraft(
            titulo,
            OpenTube.Domain.ValueObjects.Slug.From(titulo) + "-" + videoId.ToString("n")[..6],
            $"{videoId}/source.mp4",
            Guid.CreateVersion7(),
            Agora,
            descricao,
            videoId);

        video.MarkUploaded(1024);
        video.StartProcessing();
        video.MarkReady(StorageKeys.VodPrefix(videoId), 125, 640, 360, StorageKeys.Thumbnail(videoId), StorageKeys.Sprite(videoId), Agora);

        if (etiquetas is not null)
            video.ReplaceTags(etiquetas);

        if (visibilidade is not VideoVisibility.Private)
            video.ChangeVisibility(visibilidade);

        db.Videos.Add(video);
        await db.SaveChangesAsync();

        return video;
    }
}
