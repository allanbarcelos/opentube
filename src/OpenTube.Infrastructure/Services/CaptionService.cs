// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTube.Domain.Captions;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Queue;
using OpenTube.Infrastructure.Storage;
using OpenTube.Infrastructure.Transcription;

namespace OpenTube.Infrastructure.Services;

/// <summary>Parâmetros da transcrição automática enviados ao worker.</summary>
/// <param name="VideoId">Vídeo a transcrever.</param>
/// <param name="OriginalKey">Arquivo de origem.</param>
/// <param name="Language">Idioma pedido, no formato guardado (<c>pt-br</c>).</param>
/// <param name="AssetId">Legenda que recebe o resultado.</param>
public sealed record TranscriptionRequest(Guid VideoId, string OriginalKey, string Language, Guid AssetId);

/// <summary>Situação de uma legenda, para a página acompanhar o processamento.</summary>
/// <param name="Id">Legenda.</param>
/// <param name="Language">Idioma.</param>
/// <param name="Status">Situação.</param>
public sealed record CaptionStatusView(Guid Id, string Language, CaptionStatus Status);

/// <summary>
/// Legendas de um vídeo: geradas a partir da fala, enviadas ou editadas no próprio sistema.
/// Uma por idioma. O texto alimenta também a busca, que é o que torna um acervo grande
/// navegável.
/// </summary>
public class CaptionService(
    OpenTubeDbContext db,
    IStorageReader storageReader,
    IStorageWriter storageWriter,
    IJobQueue fila,
    TranscriptionAvailability disponibilidade,
    TimeProvider clock,
    ILogger<CaptionService> logger)
{
    /// <summary>Maior arquivo de legenda aceito. Legenda é texto; megabytes aqui são engano.</summary>
    public const int MaxSizeBytes = 2 * 1024 * 1024;

    /// <summary>Todas as legendas, inclusive as em processamento, para a administração.</summary>
    public Task<List<VideoAsset>> ListAsync(Guid videoId, CancellationToken cancellationToken = default) =>
        db.VideoAssets
            .AsNoTracking()
            .Where(a => a.VideoId == videoId && a.Kind == VideoAssetKind.Caption)
            .OrderBy(a => a.Language)
            .ToListAsync(cancellationToken);

    /// <summary>Só as que têm arquivo, para o player: uma legenda pedida agora ainda não tem.</summary>
    public Task<List<VideoAsset>> ListPlayableAsync(Guid videoId, CancellationToken cancellationToken = default) =>
        db.VideoAssets
            .AsNoTracking()
            .Where(a => a.VideoId == videoId && a.Kind == VideoAssetKind.Caption && a.ContentUpdatedAt != null)
            .OrderBy(a => a.Language)
            .ToListAsync(cancellationToken);

    public Task<List<CaptionStatusView>> StatusAsync(Guid videoId, CancellationToken cancellationToken = default) =>
        db.VideoAssets
            .AsNoTracking()
            .Where(a => a.VideoId == videoId && a.Kind == VideoAssetKind.Caption)
            .OrderBy(a => a.Language)
            .Select(a => new CaptionStatusView(a.Id, a.Language!, a.Status))
            .ToListAsync(cancellationToken);

    public Task<VideoAsset?> FindAsync(Guid videoId, Guid assetId, CancellationToken cancellationToken = default) =>
        db.VideoAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == assetId && a.VideoId == videoId && a.Kind == VideoAssetKind.Caption, cancellationToken);

    /// <summary>
    /// Guarda uma legenda enviada, em WebVTT ou SRT. O conteúdo é lido e reescrito em WebVTT
    /// antes: um arquivo inválido só apareceria como falha no player de quem assiste.
    /// </summary>
    public async Task<VideoAsset> UploadAsync(
        Guid videoId,
        string language,
        string? label,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (content.Length > MaxSizeBytes)
            throw new InvalidOperationException("The caption file is too large.");

        var legenda = CaptionDocument.Parse(content);

        return await GravarAsync(videoId, language, label, legenda, CaptionSource.Upload, cancellationToken);
    }

    /// <summary>Grava a versão corrigida no editor, que chega já em WebVTT.</summary>
    public async Task<VideoAsset> SaveEditAsync(
        Guid videoId, Guid assetId, string content, CancellationToken cancellationToken = default)
    {
        if (content.Length > MaxSizeBytes)
            throw new InvalidOperationException("The caption file is too large.");

        var legenda = CaptionDocument.Parse(content);

        var existente = await FindAsync(videoId, assetId, cancellationToken)
            ?? throw new InvalidOperationException("Caption not found.");

        if (existente.IsLanguagePending)
            throw new InvalidOperationException("Wait for the language to be detected.");

        return await GravarAsync(videoId, existente.Language!, null, legenda, CaptionSource.Edited, cancellationToken);
    }

    /// <summary>
    /// Cria uma legenda vazia para ser escrita no editor: o caminho quando não há transcrição
    /// automática, ou para um idioma que não é o falado no vídeo.
    /// </summary>
    public async Task<VideoAsset> CreateEmptyAsync(
        Guid videoId, string language, string? label, CancellationToken cancellationToken = default)
    {
        var idioma = IdiomaEscolhido(language);

        if (!await db.Videos.AnyAsync(v => v.Id == videoId, cancellationToken))
            throw new InvalidOperationException("Video not found");

        if (await db.VideoAssets.AnyAsync(
                a => a.VideoId == videoId && a.Kind == VideoAssetKind.Caption && a.Language == idioma, cancellationToken))
            throw new InvalidOperationException("This language already has a caption.");

        var conteudo = CaptionDocument.FromCues([]).ToWebVtt();
        var chave = StorageKeys.Caption(videoId, idioma);

        await storageWriter.PutTextAsync(StorageBucket.Vod, chave, conteudo, MediaTypes.WebVtt, cancellationToken);

        var nova = VideoAsset.CaptionWithContent(
            videoId, idioma, Rotulo(label, idioma), chave, CaptionSource.Edited,
            System.Text.Encoding.UTF8.GetByteCount(conteudo), clock.GetUtcNow());

        db.VideoAssets.Add(nova);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new InvalidOperationException("This language already has a caption.");
        }

        logger.LogInformation("Legenda {Idioma} criada vazia no vídeo {VideoId}", idioma, videoId);

        return nova;
    }

    /// <summary>
    /// Coloca a transcrição automática de um idioma na fila, a partir do original preservado.
    /// Um idioma já em processamento é recusado — inclusive quando dois pedidos chegam ao
    /// mesmo tempo: a marcação é uma atualização condicional, e a criação esbarra no índice
    /// único do idioma.
    /// </summary>
    public async Task<Guid> RequestTranscriptionAsync(
        Guid videoId, string language, string? label = null, CancellationToken cancellationToken = default)
    {
        // "auto" é aceito aqui: o Whisper detecta o idioma falado e a legenda o recebe ao terminar.
        var idioma = CaptionLanguage.Normalize(language);

        // Sem Whisper respondendo, o pedido ficaria na fila sem ninguém para atendê-lo.
        if (!(await disponibilidade.GetAsync(cancellationToken)).Available)
            throw new InvalidOperationException("Automatic transcription is not available on this server.");

        var video = await db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == videoId, cancellationToken)
            ?? throw new InvalidOperationException("Video not found");

        if (video.Status is VideoStatus.Draft)
            throw new InvalidOperationException("This video's file has not finished uploading.");

        if (!await storageReader.ExistsAsync(StorageBucket.Originals, video.OriginalKey, cancellationToken))
            throw new InvalidOperationException("The original file is no longer in storage.");

        var agora = clock.GetUtcNow();

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);

        var existente = await db.VideoAssets.AsNoTracking().FirstOrDefaultAsync(
            a => a.VideoId == videoId && a.Kind == VideoAssetKind.Caption && a.Language == idioma, cancellationToken);

        Guid legendaId;

        if (existente is null)
        {
            var nova = VideoAsset.CaptionTranscriptionRequest(
                videoId, idioma, Rotulo(label, idioma), StorageKeys.Caption(videoId, idioma), agora);

            db.VideoAssets.Add(nova);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                throw new InvalidOperationException("A transcription for this language is already in progress.");
            }

            legendaId = nova.Id;
        }
        else
        {
            var marcadas = await db.VideoAssets
                .Where(a => a.Id == existente.Id && a.Status != CaptionStatus.Processing)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, CaptionStatus.Processing)
                    .SetProperty(a => a.Error, (string?)null)
                    .SetProperty(a => a.StatusChangedAt, agora), cancellationToken);

            if (marcadas == 0)
                throw new InvalidOperationException("A transcription for this language is already in progress.");

            legendaId = existente.Id;
        }

        var trabalho = await fila.EnqueueAsync(
            JobKind.Transcript,
            videoId,
            new TranscriptionRequest(videoId, video.OriginalKey, idioma, legendaId),
            cancellationToken: cancellationToken);

        await transacao.CommitAsync(cancellationToken);

        logger.LogInformation("Transcrição {Idioma} pedida para o vídeo {VideoId} ({JobId})", idioma, videoId, trabalho);

        return legendaId;
    }

    /// <summary>
    /// Remove a legenda e o arquivo. Uma transcrição em andamento, ao terminar, não acha mais
    /// a legenda e descarta o resultado.
    /// </summary>
    public async Task DeleteAsync(Guid videoId, Guid assetId, CancellationToken cancellationToken = default)
    {
        var legenda = await db.VideoAssets.FirstOrDefaultAsync(
            a => a.Id == assetId && a.VideoId == videoId && a.Kind == VideoAssetKind.Caption, cancellationToken)
            ?? throw new InvalidOperationException("Caption not found.");

        db.VideoAssets.Remove(legenda);
        await db.SaveChangesAsync(cancellationToken);

        await storageWriter.DeleteKeysAsync(StorageBucket.Vod, [legenda.StorageKey], cancellationToken);
    }

    /// <summary>Conteúdo de uma legenda, para ser servido ou baixado. Nulo enquanto não houver arquivo.</summary>
    public async Task<(VideoAsset Asset, string Content)?> ReadAsync(
        Guid assetId, CancellationToken cancellationToken = default)
    {
        var legenda = await db.VideoAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == assetId && a.Kind == VideoAssetKind.Caption, cancellationToken);

        if (legenda is null || !legenda.HasContent)
            return null;

        var conteudo = await storageReader.GetTextAsync(StorageBucket.Vod, legenda.StorageKey, cancellationToken);

        return (legenda, conteudo);
    }

    /// <summary>Verifica se o conteúdo é um WebVTT reconhecível.</summary>
    public static bool EhWebVtt(string? content) =>
        !string.IsNullOrWhiteSpace(content) &&
        content.TrimStart('﻿').TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal);

    private async Task<VideoAsset> GravarAsync(
        Guid videoId, string language, string? label, CaptionDocument legenda, CaptionSource origem, CancellationToken cancellationToken)
    {
        var idioma = IdiomaEscolhido(language);

        var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, cancellationToken)
            ?? throw new InvalidOperationException("Video not found");

        var existente = await db.VideoAssets.FirstOrDefaultAsync(
            a => a.VideoId == videoId && a.Kind == VideoAssetKind.Caption && a.Language == idioma, cancellationToken);

        // Conferido antes de gravar o arquivo: durante a transcrição, a gravação seria
        // sobrescrita pelo resultado dela.
        if (existente?.IsProcessing == true)
            throw new InvalidOperationException("Wait for the transcription of this language to finish.");

        var conteudo = legenda.ToWebVtt();
        var chave = existente?.StorageKey ?? StorageKeys.Caption(videoId, idioma);
        var agora = clock.GetUtcNow();

        await storageWriter.PutTextAsync(StorageBucket.Vod, chave, conteudo, MediaTypes.WebVtt, cancellationToken);

        if (existente is null)
        {
            existente = VideoAsset.CaptionWithContent(
                videoId, idioma, Rotulo(label, idioma), chave, origem, System.Text.Encoding.UTF8.GetByteCount(conteudo), agora);
            db.VideoAssets.Add(existente);
        }
        else
        {
            existente.ReplaceContent(origem, System.Text.Encoding.UTF8.GetByteCount(conteudo), agora);
            existente.Relabel(label);
        }

        // A fala da legenda mais recente alimenta a busca.
        video.SetTranscript(legenda.PlainText);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Legenda {Idioma} gravada no vídeo {VideoId} ({Origem})", idioma, videoId, origem);

        return existente;
    }

    /// <summary>Envio e edição precisam de um idioma de fato: "auto" só existe na transcrição.</summary>
    private static string IdiomaEscolhido(string language) =>
        CaptionLanguage.IsAuto(language)
            ? throw new InvalidOperationException("Choose the caption language.")
            : CaptionLanguage.Normalize(language);

    private static string Rotulo(string? label, string idioma) =>
        string.IsNullOrWhiteSpace(label) ? CaptionLanguage.DisplayName(idioma) : label.Trim();
}
