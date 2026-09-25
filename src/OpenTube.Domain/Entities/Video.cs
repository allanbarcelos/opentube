using OpenTube.Domain.Enums;

namespace OpenTube.Domain.Entities;

/// <summary>
/// Um vídeo do acervo. Nasce privado e em rascunho; só fica reproduzível depois que o worker
/// conclui a transcodificação.
/// </summary>
public class Video
{
    private readonly List<VideoAsset> _assets = [];
    private readonly List<string> _tags = [];

    private Video() { }

    public Guid Id { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string Slug { get; private set; } = string.Empty;

    public VideoVisibility Visibility { get; private set; }
    public VideoStatus Status { get; private set; }

    /// <summary>Chave do arquivo enviado, no bucket de originais.</summary>
    public string OriginalKey { get; private set; } = string.Empty;

    /// <summary>Prefixo das saídas HLS no bucket de distribuição; nulo enquanto não processado.</summary>
    public string? HlsPrefix { get; private set; }

    public string? ThumbnailKey { get; private set; }
    public string? SpriteKey { get; private set; }

    public double DurationSeconds { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long SizeBytes { get; private set; }

    /// <summary>Texto reconhecido do áudio, preenchido pela fase de legendas automáticas.</summary>
    public string? Transcript { get; private set; }

    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    public IReadOnlyList<string> Tags => _tags;
    public IReadOnlyCollection<VideoAsset> Assets => _assets;

    public bool IsDeleted => DeletedAt is not null;
    public bool IsPlayable => Status == VideoStatus.Ready && !IsDeleted;

    /// <summary>
    /// Cria o rascunho. O identificador pode ser informado porque o caminho do arquivo no
    /// storage é montado a partir dele, antes de o registro existir.
    /// </summary>
    public static Video CreateDraft(string title, string slug, string originalKey, Guid createdBy, DateTimeOffset now, string? description = null, Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalKey);

        return new Video
        {
            Id = id ?? Guid.CreateVersion7(),
            Title = title.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Slug = slug,
            OriginalKey = originalKey,
            Visibility = VideoVisibility.Private,
            Status = VideoStatus.Draft,
            CreatedBy = createdBy,
            CreatedAt = now
        };
    }

    public void Describe(string title, string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Title = title.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    public void ReplaceTags(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        _tags.Clear();
        _tags.AddRange(tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(30));
    }

    /// <summary>Chamado quando o navegador conclui o envio multipart do arquivo original.</summary>
    public void MarkUploaded(long sizeBytes)
    {
        if (Status is not VideoStatus.Draft)
            throw new InvalidOperationException($"Só um rascunho pode receber o arquivo enviado; o vídeo está em '{Status}'.");
        if (sizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "O arquivo enviado não pode ser vazio.");

        SizeBytes = sizeBytes;
        Status = VideoStatus.Uploaded;
    }

    /// <summary>
    /// Coloca o vídeo em processamento. Um vídeo já pronto pode ser reprocessado a partir do
    /// original — é o que permite gerar novas versões sem pedir outro upload.
    /// </summary>
    public void StartProcessing()
    {
        if (Status is not (VideoStatus.Uploaded or VideoStatus.Failed or VideoStatus.Ready))
            throw new InvalidOperationException($"Não é possível processar um vídeo em '{Status}'.");

        Status = VideoStatus.Processing;
    }

    public void MarkReady(string hlsPrefix, double durationSeconds, int width, int height, string? thumbnailKey, string? spriteKey, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hlsPrefix);
        if (Status is not VideoStatus.Processing)
            throw new InvalidOperationException($"Só um vídeo em processamento pode ficar pronto; o estado é '{Status}'.");
        if (durationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "A duração precisa ser positiva.");

        HlsPrefix = hlsPrefix;
        DurationSeconds = durationSeconds;
        Width = width;
        Height = height;
        ThumbnailKey = thumbnailKey;
        SpriteKey = spriteKey;
        Status = VideoStatus.Ready;
        PublishedAt ??= now;
    }

    public void MarkFailed()
    {
        if (Status is VideoStatus.Ready)
            throw new InvalidOperationException("Um vídeo já pronto não volta para falha; reprocesse a partir do original.");

        Status = VideoStatus.Failed;
    }

    /// <summary>
    /// Altera a visibilidade. Só faz sentido para um vídeo reproduzível: liberar um vídeo que
    /// ainda está processando criaria um link quebrado visível para todo mundo.
    /// </summary>
    public void ChangeVisibility(VideoVisibility visibility)
    {
        if (visibility is not VideoVisibility.Private && !IsPlayable)
            throw new InvalidOperationException("Só um vídeo pronto pode deixar de ser privado.");

        Visibility = visibility;
    }

    public void SetTranscript(string? transcript) =>
        Transcript = string.IsNullOrWhiteSpace(transcript) ? null : transcript.Trim();

    public void AddAsset(VideoAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        _assets.Add(asset);
    }

    /// <summary>
    /// Exclusão lógica: o histórico de visualizações precisa sobreviver ao vídeo, senão o
    /// relatório de quem assistiu o quê perde sentido retroativamente.
    /// </summary>
    public void SoftDelete(DateTimeOffset now)
    {
        DeletedAt ??= now;
        Visibility = VideoVisibility.Private;
    }

    public void Restore() => DeletedAt = null;
}
