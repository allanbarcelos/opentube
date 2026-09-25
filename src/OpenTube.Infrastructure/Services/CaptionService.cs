using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Queue;
using OpenTube.Infrastructure.Storage;

namespace OpenTube.Infrastructure.Services;

/// <summary>Parâmetros da transcrição automática enviados ao worker.</summary>
/// <param name="VideoId">Vídeo a transcrever.</param>
/// <param name="OriginalKey">Arquivo de origem.</param>
public sealed record TranscriptionRequest(Guid VideoId, string OriginalKey);

/// <summary>
/// Legendas de um vídeo, enviadas à mão ou geradas a partir da fala. O texto alimenta também
/// a busca, que é o que torna um acervo grande navegável.
/// </summary>
public class CaptionService(
    OpenTubeDbContext db,
    IVideoStorage storage,
    IJobQueue fila,
    TimeProvider clock,
    ILogger<CaptionService> logger)
{
    /// <summary>Maior arquivo de legenda aceito. Legenda é texto; megabytes aqui são engano.</summary>
    public const int MaxSizeBytes = 2 * 1024 * 1024;

    public Task<List<VideoAsset>> ListAsync(Guid videoId, CancellationToken cancellationToken = default) =>
        db.VideoAssets
            .AsNoTracking()
            .Where(a => a.VideoId == videoId && a.Kind == VideoAssetKind.Caption)
            .OrderBy(a => a.Language)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Guarda uma legenda enviada à mão. O conteúdo é conferido antes: um arquivo que não é
    /// WebVTT só apareceria como falha no player de quem assiste.
    /// </summary>
    public async Task<VideoAsset> UploadAsync(
        Guid videoId,
        string language,
        string? label,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("The caption file is empty.");

        if (content.Length > MaxSizeBytes)
            throw new InvalidOperationException("The caption file is too large.");

        if (!EhWebVtt(content))
            throw new InvalidOperationException("The file must be WebVTT and start with WEBVTT.");

        var video = await db.Videos.Include(v => v.Assets).FirstOrDefaultAsync(v => v.Id == videoId, cancellationToken)
            ?? throw new InvalidOperationException("Video not found");

        var idioma = language.Trim().ToLowerInvariant();
        var chave = StorageKeys.Caption(videoId, idioma);

        await storage.PutTextAsync(StorageBucket.Vod, chave, content, MediaTypes.WebVtt, cancellationToken);

        var existente = await db.VideoAssets.FirstOrDefaultAsync(
            a => a.VideoId == videoId && a.Kind == VideoAssetKind.Caption && a.StorageKey == chave, cancellationToken);

        if (existente is not null)
        {
            logger.LogInformation("Legenda {Idioma} substituída no vídeo {VideoId}", idioma, videoId);
            return existente;
        }

        var legenda = VideoAsset.Create(
            videoId, VideoAssetKind.Caption, chave, clock.GetUtcNow(), idioma,
            string.IsNullOrWhiteSpace(label) ? idioma : label, content.Length);

        db.VideoAssets.Add(legenda);
        await db.SaveChangesAsync(cancellationToken);

        return legenda;
    }

    public async Task DeleteAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        var legenda = await db.VideoAssets.FirstOrDefaultAsync(
            a => a.Id == assetId && a.Kind == VideoAssetKind.Caption, cancellationToken)
            ?? throw new InvalidOperationException("Caption not found.");

        db.VideoAssets.Remove(legenda);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Coloca a transcrição automática na fila, a partir do original preservado.</summary>
    public async Task<Guid> RequestTranscriptionAsync(Guid videoId, CancellationToken cancellationToken = default)
    {
        var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, cancellationToken)
            ?? throw new InvalidOperationException("Video not found");

        if (video.Status is VideoStatus.Draft)
            throw new InvalidOperationException("This video's file has not finished uploading.");

        if (!await storage.ExistsAsync(StorageBucket.Originals, video.OriginalKey, cancellationToken))
            throw new InvalidOperationException("The original file is no longer in storage.");

        return await fila.EnqueueAsync(
            JobKind.Transcript,
            video.Id,
            new TranscriptionRequest(video.Id, video.OriginalKey),
            cancellationToken: cancellationToken);
    }

    /// <summary>Conteúdo de uma legenda, para ser servido a quem tem acesso ao vídeo.</summary>
    public async Task<(VideoAsset Asset, string Content)?> ReadAsync(
        Guid assetId, CancellationToken cancellationToken = default)
    {
        var legenda = await db.VideoAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == assetId && a.Kind == VideoAssetKind.Caption, cancellationToken);

        if (legenda is null)
            return null;

        var conteudo = await storage.GetTextAsync(StorageBucket.Vod, legenda.StorageKey, cancellationToken);

        return (legenda, conteudo);
    }

    /// <summary>Verifica se o conteúdo é um WebVTT reconhecível.</summary>
    public static bool EhWebVtt(string? content) =>
        !string.IsNullOrWhiteSpace(content) &&
        content.TrimStart('﻿').TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal);
}
