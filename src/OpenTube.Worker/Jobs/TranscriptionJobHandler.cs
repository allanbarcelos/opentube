// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Captions;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Queue;
using OpenTube.Infrastructure.Storage;
using OpenTube.Worker.Media;

namespace OpenTube.Worker.Jobs;

/// <summary>Parâmetros da transcrição.</summary>
/// <param name="VideoId">Vídeo a transcrever.</param>
/// <param name="OriginalKey">Arquivo de origem.</param>
/// <param name="Language">Idioma pedido (<c>pt-br</c>). Ausente em trabalhos antigos na fila.</param>
/// <param name="AssetId">Legenda que recebe o resultado. Ausente em trabalhos antigos na fila.</param>
public sealed record TranscriptionPayload(Guid VideoId, string OriginalKey, string? Language = null, Guid? AssetId = null);

/// <summary>
/// Gera a legenda de um idioma a partir da fala e alimenta a busca com o texto. A legenda
/// pedida fica "processando" até aqui terminar: pronta com o resultado, ou com a falha
/// registrada depois da última tentativa.
/// </summary>
public class TranscriptionJobHandler(
    OpenTubeDbContext db,
    IVideoStorage storage,
    ITranscriber transcritor,
    IOptions<TranscriptionOptions> opcoes,
    TimeProvider clock,
    ILogger<TranscriptionJobHandler> logger) : IJobHandler
{
    public JobKind Kind => JobKind.Transcript;

    public async Task HandleAsync(QueuedJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var payload = job.PayloadAs<TranscriptionPayload>()
            ?? throw new InvalidOperationException("Parâmetros de transcrição ausentes.");

        var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == payload.VideoId, cancellationToken)
            ?? throw new InvalidOperationException($"Vídeo {payload.VideoId} não encontrado.");

        var idioma = CaptionLanguage.Normalize(payload.Language ?? opcoes.Value.Language);
        var legenda = await LegendaDoPedidoAsync(payload, video.Id, idioma, cancellationToken);

        if (legenda is null)
        {
            logger.LogInformation("Legenda do pedido {JobId} foi removida; transcrição descartada", job.Id);
            return;
        }

        if (video.IsDeleted)
        {
            await FalharAsync(legenda, "The video was deleted.");
            return;
        }

        // Sem a ferramenta, tentar de novo não adianta: a falha é registrada de uma vez.
        if (!transcritor.IsAvailable)
        {
            await FalharAsync(legenda, "Automatic transcription is not configured on this server.");
            return;
        }

        var trabalho = Directory.CreateTempSubdirectory($"opentube-legenda-{video.Id:n}-");

        try
        {
            var original = Path.Combine(trabalho.FullName, "original" + StorageKeys.SafeExtension(payload.OriginalKey));
            await storage.GetFileAsync(StorageBucket.Originals, payload.OriginalKey, original, cancellationToken);

            var transcricao = await transcritor.TranscribeAsync(
                original, trabalho.FullName, CaptionLanguage.TranscriptionCode(idioma), cancellationToken);

            // O que a ferramenta produziu passa pelo mesmo leitor do editor e do envio: o que
            // fica guardado é sempre um WebVTT válido e ordenado.
            var documento = CaptionDocument.Parse(await File.ReadAllTextAsync(transcricao.VttPath, cancellationToken));
            var conteudo = documento.ToWebVtt();

            var destino = legenda;

            if (legenda.IsLanguagePending)
            {
                destino = await DestinoDoIdiomaDetectadoAsync(legenda, video.Id, transcricao.Language, cancellationToken);
                if (destino is null)
                    return;
            }

            await storage.PutTextAsync(StorageBucket.Vod, destino.StorageKey, conteudo, MediaTypes.WebVtt, cancellationToken);

            destino.CompleteTranscription(System.Text.Encoding.UTF8.GetByteCount(conteudo), clock.GetUtcNow());
            video.SetTranscript(documento.PlainText);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
            {
                // Alguém criou a legenda do idioma detectado enquanto esta transcrevia.
                db.ChangeTracker.Clear();
                await FalharAsync(legenda.Id, "A caption in the detected language was created meanwhile.");
                return;
            }

            logger.LogInformation("Legenda {Idioma} gerada para o vídeo {VideoId}", destino.Language, video.Id);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Antes da última tentativa a legenda continua "processando": a fila tenta de novo.
            if (job.Attempts >= ProcessingJob.MaxAttempts)
                await FalharAsync(legenda, e.Message);

            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(trabalho.FullName))
                    Directory.Delete(trabalho.FullName, recursive: true);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Não foi possível limpar a pasta de trabalho da transcrição");
            }
        }
    }

    /// <summary>
    /// Legenda que recebe o resultado. Trabalhos antigos na fila não traziam a legenda: ela é
    /// encontrada pelo idioma, ou criada já em processamento.
    /// </summary>
    private async Task<VideoAsset?> LegendaDoPedidoAsync(
        TranscriptionPayload payload, Guid videoId, string idioma, CancellationToken cancellationToken)
    {
        if (payload.AssetId is { } id)
            return await db.VideoAssets.FirstOrDefaultAsync(a => a.Id == id && a.Kind == VideoAssetKind.Caption, cancellationToken);

        var existente = await db.VideoAssets.FirstOrDefaultAsync(
            a => a.VideoId == videoId && a.Kind == VideoAssetKind.Caption && a.Language == idioma, cancellationToken);

        if (existente is not null)
            return existente;

        var nova = VideoAsset.CaptionTranscriptionRequest(videoId, idioma, null, StorageKeys.Caption(videoId, idioma), clock.GetUtcNow());
        db.VideoAssets.Add(nova);

        return nova;
    }

    /// <summary>
    /// Na detecção automática, a legenda provisória ("auto") vira a do idioma falado. Se esse
    /// idioma já tem legenda gerada automaticamente, ela recebe o resultado e a provisória sai;
    /// uma legenda enviada ou corrigida à mão nunca é sobrescrita, nem uma ainda em processamento.
    /// </summary>
    private async Task<VideoAsset?> DestinoDoIdiomaDetectadoAsync(
        VideoAsset provisoria, Guid videoId, string detectado, CancellationToken cancellationToken)
    {
        var codigo = CaptionLanguage.Normalize(CaptionLanguage.IsAuto(detectado) ? opcoes.Value.Language : detectado);

        var existente = await db.VideoAssets.FirstOrDefaultAsync(
            a => a.VideoId == videoId && a.Kind == VideoAssetKind.Caption && a.Language == codigo && a.Id != provisoria.Id,
            cancellationToken);

        if (existente is null)
        {
            provisoria.ResolveLanguage(codigo, StorageKeys.Caption(videoId, codigo), CaptionLanguage.DisplayName(codigo));
            return provisoria;
        }

        if (existente.IsProcessing || existente.Source is not CaptionSource.Automatic)
        {
            await FalharAsync(provisoria, existente.IsProcessing
                ? "The detected language is already being transcribed."
                : "The detected language already has a caption. Delete it or use Regenerate on it.");
            return null;
        }

        db.VideoAssets.Remove(provisoria);

        logger.LogInformation("Idioma detectado {Idioma}: a legenda automática existente recebe o resultado", codigo);
        return existente;
    }

    private async Task FalharAsync(Guid legendaId, string motivo)
    {
        var legenda = await db.VideoAssets.FirstOrDefaultAsync(a => a.Id == legendaId, CancellationToken.None);
        if (legenda is not null)
            await FalharAsync(legenda, motivo);
    }

    /// <summary>Registra a falha mesmo com o desligamento já pedido: a marcação precisa ficar.</summary>
    private async Task FalharAsync(VideoAsset legenda, string motivo)
    {
        legenda.FailTranscription(motivo, clock.GetUtcNow());
        await db.SaveChangesAsync(CancellationToken.None);

        logger.LogWarning("Transcrição da legenda {LegendaId} falhou: {Motivo}", legenda.Id, motivo);
    }
}
