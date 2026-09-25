using OpenTube.Domain.Enums;

namespace OpenTube.Domain.Entities;

/// <summary>Arquivo derivado ou anexo associado a um vídeo (legenda, capítulos, material de apoio).</summary>
public class VideoAsset
{
    private VideoAsset() { }

    public Guid Id { get; private set; }
    public Guid VideoId { get; private set; }
    public VideoAssetKind Kind { get; private set; }

    /// <summary>Código de idioma no formato BCP 47 (`pt-BR`), quando aplicável.</summary>
    public string? Language { get; private set; }

    public string StorageKey { get; private set; } = string.Empty;
    public string? Label { get; private set; }
    public long SizeBytes { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static VideoAsset Create(Guid videoId, VideoAssetKind kind, string storageKey, DateTimeOffset now, string? language = null, string? label = null, long sizeBytes = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        return new VideoAsset
        {
            Id = Guid.CreateVersion7(),
            VideoId = videoId,
            Kind = kind,
            StorageKey = storageKey,
            Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim(),
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            SizeBytes = sizeBytes,
            CreatedAt = now
        };
    }
}
