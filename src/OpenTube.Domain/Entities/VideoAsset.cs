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

    // Estado das legendas. Os demais tipos de arquivo não passam por processamento próprio.

    public CaptionStatus Status { get; private set; }
    public CaptionSource Source { get; private set; }

    /// <summary>Motivo da última falha de transcrição, para a administração ver.</summary>
    public string? Error { get; private set; }

    /// <summary>Quando o conteúdo foi gravado pela última vez. Nulo: ainda não há conteúdo.</summary>
    public DateTimeOffset? ContentUpdatedAt { get; private set; }

    public DateTimeOffset StatusChangedAt { get; private set; }

    /// <summary>
    /// Se há arquivo para entregar. Uma legenda pedida pela primeira vez fica sem conteúdo até
    /// a transcrição terminar; uma regerada continua servindo a versão anterior enquanto isso.
    /// </summary>
    public bool HasContent => ContentUpdatedAt is not null;

    public bool IsProcessing => Status is CaptionStatus.Processing;

    /// <summary>Legenda com conteúdo já gravado: enviada ou editada.</summary>
    public static VideoAsset CaptionWithContent(
        Guid videoId, string language, string? label, string storageKey, CaptionSource source, long sizeBytes, DateTimeOffset now)
    {
        var legenda = NovaLegenda(videoId, language, label, storageKey, now);
        legenda.Source = source;
        legenda.SizeBytes = sizeBytes;
        legenda.ContentUpdatedAt = now;

        return legenda;
    }

    /// <summary>Legenda pedida à transcrição automática, ainda sem conteúdo.</summary>
    public static VideoAsset CaptionTranscriptionRequest(
        Guid videoId, string language, string? label, string storageKey, DateTimeOffset now)
    {
        var legenda = NovaLegenda(videoId, language, label, storageKey, now);
        legenda.Status = CaptionStatus.Processing;
        legenda.Source = CaptionSource.Automatic;

        return legenda;
    }

    /// <summary>Pede de novo a transcrição de uma legenda existente.</summary>
    public void StartTranscription(DateTimeOffset now)
    {
        GarantirLegenda();

        if (IsProcessing)
            throw new InvalidOperationException("A transcription for this language is already in progress.");

        Status = CaptionStatus.Processing;
        Error = null;
        StatusChangedAt = now;
    }

    public void CompleteTranscription(long sizeBytes, DateTimeOffset now)
    {
        GarantirLegenda();

        Status = CaptionStatus.Ready;
        Source = CaptionSource.Automatic;
        Error = null;
        SizeBytes = sizeBytes;
        ContentUpdatedAt = now;
        StatusChangedAt = now;
    }

    /// <summary>Registra a falha. O conteúdo anterior, se havia, continua valendo.</summary>
    public void FailTranscription(string error, DateTimeOffset now)
    {
        GarantirLegenda();

        Status = CaptionStatus.Failed;
        Error = string.IsNullOrWhiteSpace(error) ? "Transcription failed." : error.Trim()[..Math.Min(error.Trim().Length, 500)];
        StatusChangedAt = now;
    }

    /// <summary>
    /// Troca o conteúdo por um envio ou uma edição. Recusado durante a transcrição: o
    /// resultado dela sobrescreveria o que a pessoa acabou de fazer.
    /// </summary>
    public void ReplaceContent(CaptionSource source, long sizeBytes, DateTimeOffset now)
    {
        GarantirLegenda();

        if (IsProcessing)
            throw new InvalidOperationException("Wait for the transcription of this language to finish.");

        Status = CaptionStatus.Ready;
        Source = source;
        Error = null;
        SizeBytes = sizeBytes;
        ContentUpdatedAt = now;
        StatusChangedAt = now;
    }

    public void Relabel(string? label)
    {
        if (!string.IsNullOrWhiteSpace(label))
            Label = label.Trim();
    }

    private static VideoAsset NovaLegenda(Guid videoId, string language, string? label, string storageKey, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        var legenda = Create(videoId, VideoAssetKind.Caption, storageKey, now, language.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(label) ? language : label);
        legenda.StatusChangedAt = now;

        return legenda;
    }

    private void GarantirLegenda()
    {
        if (Kind is not VideoAssetKind.Caption)
            throw new InvalidOperationException("Only captions have a processing state.");
    }

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
