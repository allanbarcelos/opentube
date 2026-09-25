namespace OpenTube.Domain.Entities;

/// <summary>
/// Agrupamento de vídeos. Existe para que a liberação de acesso recaia sobre um conjunto:
/// conceder vídeo a vídeo funciona com três, mas vira trabalho manual com duzentos, e
/// acrescentar o quarto vídeo passaria a exigir uma nova concessão.
/// </summary>
public class Collection
{
    private readonly List<CollectionVideo> _videos = [];

    private Collection() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    public IReadOnlyCollection<CollectionVideo> Videos => _videos;

    public bool IsDeleted => DeletedAt is not null;

    public static Collection Create(string name, string slug, Guid createdBy, DateTimeOffset now, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        return new Collection
        {
            Id = Guid.CreateVersion7(),
            Name = name.Trim(),
            Slug = slug,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            CreatedBy = createdBy,
            CreatedAt = now
        };
    }

    public void Rename(string name, string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    /// <summary>Acrescenta um vídeo ao fim da coleção, sem duplicar.</summary>
    public void Add(Guid videoId)
    {
        if (_videos.Any(v => v.VideoId == videoId))
            return;

        _videos.Add(CollectionVideo.Create(Id, videoId, ProximaPosicao()));
    }

    public void Remove(Guid videoId) => _videos.RemoveAll(v => v.VideoId == videoId);

    /// <summary>Redefine o conteúdo da coleção na ordem informada.</summary>
    public void Replace(IEnumerable<Guid> videoIds)
    {
        ArgumentNullException.ThrowIfNull(videoIds);

        _videos.Clear();

        var posicao = 0;
        foreach (var videoId in videoIds.Distinct())
            _videos.Add(CollectionVideo.Create(Id, videoId, posicao++));
    }

    public void SoftDelete(DateTimeOffset now) => DeletedAt ??= now;

    public void Restore() => DeletedAt = null;

    private int ProximaPosicao() => _videos.Count == 0 ? 0 : _videos.Max(v => v.Position) + 1;
}

/// <summary>Vínculo entre uma coleção e um vídeo, com a ordem de exibição.</summary>
public class CollectionVideo
{
    private CollectionVideo() { }

    public Guid CollectionId { get; private set; }
    public Guid VideoId { get; private set; }
    public int Position { get; private set; }

    public static CollectionVideo Create(Guid collectionId, Guid videoId, int position) => new()
    {
        CollectionId = collectionId,
        VideoId = videoId,
        Position = position
    };
}
