// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.Media;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Localization;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Queue;
using OpenTube.Infrastructure.Storage;

namespace OpenTube.Infrastructure.Services;

/// <summary>Dados que o navegador precisa para enviar o arquivo direto ao storage.</summary>
/// <param name="VideoId">Registro criado para o vídeo.</param>
/// <param name="UploadId">Identificador do envio multipart no storage.</param>
/// <param name="StorageKey">Caminho do arquivo no bucket de originais.</param>
/// <param name="PartSizeBytes">Tamanho de cada pedaço.</param>
/// <param name="PartCount">Quantidade de pedaços.</param>
/// <param name="Parts">URLs assinadas dos primeiros pedaços.</param>
public sealed record UploadTicket(
    Guid VideoId,
    string UploadId,
    string StorageKey,
    int PartSizeBytes,
    int PartCount,
    IReadOnlyList<UploadPartUrl> Parts);

/// <summary>Parâmetros de transcodificação enviados ao worker.</summary>
/// <param name="VideoId">Vídeo a processar.</param>
/// <param name="OriginalKey">Arquivo de origem no bucket de originais.</param>
public sealed record TranscodePayload(Guid VideoId, string OriginalKey);

/// <summary>Geração publicada cuja saída antiga pode ser apagada quando o prazo das URLs vencer.</summary>
/// <param name="VideoId">Vídeo dono das saídas.</param>
/// <param name="Prefix">Prefixo que precisa continuar sendo o publicado para a limpeza valer.</param>
public sealed record RetireOutputsPayload(Guid VideoId, string Prefix);

/// <summary>
/// Conduz o envio de um vídeo: cria o registro, abre o envio multipart, assina os pedaços e,
/// ao fim, enfileira a transcodificação. O arquivo nunca passa pela aplicação.
/// </summary>
public class VideoUploadService(
    OpenTubeDbContext db,
    IVideoStorage storage,
    IJobQueue queue,
    IOptions<StorageOptions> options,
    TimeProvider clock,
    ILogger<VideoUploadService> logger)
{
    /// <summary>Quantas URLs de pedaço são assinadas de uma vez.</summary>
    public const int PartUrlBatchSize = 50;

    private readonly StorageOptions _options = options.Value;

    /// <param name="title">Título; vazio usa o nome do arquivo sem a extensão.</param>
    /// <param name="collectionId">
    /// Coleção que recebe o vídeo quando o envio terminar (o envio de uma pasta). Conferida já
    /// aqui, para não gastar o envio inteiro e só então descobrir que ela não existe.
    /// </param>
    public async Task<UploadTicket> StartAsync(
        string? title,
        string? description,
        string fileName,
        string? contentType,
        long fileSizeBytes,
        Guid adminId,
        Guid? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        title = string.IsNullOrWhiteSpace(title) ? UploadNames.TitleFromFileName(fileName) : title.Trim();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fileSizeBytes, 0);

        if (fileSizeBytes > _options.MaxUploadBytes)
            throw new InvalidOperationException(LocalText.Format(
                "The file exceeds the limit of {0} GiB.", _options.MaxUploadBytes / (1024L * 1024 * 1024)));

        if (!MediaTypes.LooksLikeVideo(fileName, contentType))
            throw new InvalidOperationException("The file does not look like a video.");

        if (collectionId is { } colecaoId)
            await CarregarColecaoAsync(colecaoId, cancellationToken);

        var slug = await GerarSlugUnicoAsync(title, cancellationToken);
        var videoId = Guid.CreateVersion7();
        var chave = StorageKeys.Original(videoId, fileName);

        var uploadId = await storage.StartUploadAsync(chave, contentType ?? MediaTypes.Mp4, cancellationToken);
        var plano = UploadPlan.For(fileSizeBytes);

        var video = Video.CreateDraft(title, slug, chave, adminId, clock.GetUtcNow(), description, videoId);

        db.Videos.Add(video);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Envio iniciado para o vídeo {VideoId} em {Partes} pedaços", videoId, plano.PartCount);

        return new UploadTicket(
            videoId,
            uploadId,
            chave,
            plano.PartSizeBytes,
            plano.PartCount,
            storage.SignUploadParts(chave, uploadId, 1, Math.Min(PartUrlBatchSize, plano.PartCount)));
    }

    /// <summary>
    /// Assina mais um lote de pedaços. Enviar arquivos grandes exige renovar as URLs conforme
    /// o navegador avança, em vez de assinar dez mil de uma vez.
    /// </summary>
    public async Task<IReadOnlyList<UploadPartUrl>> SignMorePartsAsync(
        Guid videoId,
        string uploadId,
        int firstPart,
        int partCount,
        CancellationToken cancellationToken = default)
    {
        var video = await CarregarRascunhoAsync(videoId, cancellationToken);

        return storage.SignUploadParts(video.OriginalKey, uploadId, firstPart, Math.Min(partCount, PartUrlBatchSize));
    }

    /// <param name="collectionId">
    /// Coleção que recebe o vídeo. Só um envio concluído entra nela: um cancelado some junto
    /// com o rascunho, sem deixar vínculo para trás. Como a pasta é enviada um arquivo por vez,
    /// a ordem na coleção é a ordem do envio.
    /// </param>
    public async Task<Video> CompleteAsync(
        Guid videoId,
        string uploadId,
        IEnumerable<CompletedPart> parts,
        Guid? collectionId = null,
        CancellationToken cancellationToken = default,
        string? title = null)
    {
        var video = await CarregarRascunhoAsync(videoId, cancellationToken);
        var colecao = collectionId is { } colecaoId ? await CarregarColecaoAsync(colecaoId, cancellationToken) : null;

        // O envio começa assim que o arquivo entra na fila; o título pode ter sido editado
        // enquanto ele subia, e vale o que estiver escrito ao terminar.
        if (!string.IsNullOrWhiteSpace(title))
        {
            var titulo = title.Trim();
            if (titulo.Length > UploadNames.MaxTitleLength)
                titulo = titulo[..UploadNames.MaxTitleLength].TrimEnd();
            if (titulo != video.Title)
                video.Describe(titulo, video.Description);
        }

        var tamanho = await storage.CompleteUploadAsync(video.OriginalKey, uploadId, parts, cancellationToken);

        // Marcar o vídeo como enviado e agendar o processamento vão juntos: sem a transação,
        // uma queda entre os dois deixaria o vídeo "enviado" sem nenhum trabalho na fila, preso
        // até alguém reprocessar à mão. É o mesmo cuidado de RequestTranscriptionAsync.
        await using var transacao = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        video.MarkUploaded(tamanho);
        colecao?.Add(video.Id, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);

        await queue.EnqueueAsync(
            JobKind.Transcode,
            video.Id,
            new TranscodePayload(video.Id, video.OriginalKey),
            cancellationToken: cancellationToken);

        if (transacao is not null)
            await transacao.CommitAsync(cancellationToken);

        logger.LogInformation("Envio concluído para o vídeo {VideoId} ({Bytes} bytes)", videoId, tamanho);

        return video;
    }

    /// <summary>Desfaz um envio abandonado, liberando os pedaços e apagando o rascunho.</summary>
    public async Task AbortAsync(Guid videoId, string uploadId, CancellationToken cancellationToken = default)
    {
        var video = await CarregarRascunhoAsync(videoId, cancellationToken);

        await storage.AbortUploadAsync(video.OriginalKey, uploadId, cancellationToken);

        db.Videos.Remove(video);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Video> CarregarRascunhoAsync(Guid videoId, CancellationToken cancellationToken)
    {
        var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, cancellationToken)
            ?? throw new InvalidOperationException(LocalText.Format("Video {0} was not found.", videoId));

        if (video.Status is not VideoStatus.Draft)
            throw new InvalidOperationException(LocalText.Format("Video {0} is no longer a draft.", videoId));

        return video;
    }

    private async Task<Collection> CarregarColecaoAsync(Guid collectionId, CancellationToken cancellationToken)
    {
        var colecao = await db.Collections.Include(c => c.Videos).FirstOrDefaultAsync(c => c.Id == collectionId, cancellationToken);

        if (colecao is null || colecao.IsDeleted)
            throw new InvalidOperationException("The collection was not found.");

        return colecao;
    }

    private async Task<string> GerarSlugUnicoAsync(string title, CancellationToken cancellationToken)
    {
        var baseSlug = Slug.From(title);
        if (baseSlug.Length == 0)
            baseSlug = "video";

        // Traz de uma vez os endereços parecidos, em vez de consultar o banco a cada tentativa.
        var ocupados = await db.Videos
            .Where(v => v.Slug == baseSlug || v.Slug.StartsWith(baseSlug + "-"))
            .Select(v => v.Slug)
            .ToListAsync(cancellationToken);

        var conjunto = ocupados.ToHashSet(StringComparer.Ordinal);

        return Slug.Unique(title, conjunto.Contains);
    }
}
