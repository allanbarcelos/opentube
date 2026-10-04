// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Storage;
using OpenTube.Shared.Catalog;

namespace OpenTube.Infrastructure.Services;

/// <summary>Coleção com o que a listagem administrativa precisa mostrar.</summary>
/// <param name="Id">Identificador.</param>
/// <param name="Name">Nome.</param>
/// <param name="Slug">Endereço legível.</param>
/// <param name="Description">Descrição.</param>
/// <param name="VideoCount">Quantidade de vídeos.</param>
/// <param name="GrantCount">Quantidade de concessões em vigor sobre a coleção.</param>
/// <param name="IsDeleted">Se está excluída.</param>
public sealed record CollectionSummary(
    Guid Id, string Name, string Slug, string? Description, int VideoCount, int GrantCount, bool IsDeleted);

/// <summary>O que aconteceu ao pedir para colocar um vídeo na coleção.</summary>
/// <param name="NeedsConfirmation">Ele já está em outra coleção e nada foi alterado.</param>
/// <param name="Moved">Ele saiu da coleção anterior e entrou nesta.</param>
/// <param name="OtherCollections">Nomes das coleções de onde ele veio, ou de onde viria.</param>
public sealed record AddVideoResult(bool NeedsConfirmation, bool Moved, IReadOnlyList<string> OtherCollections)
{
    public static AddVideoResult Added { get; } = new(false, false, []);

    public static AddVideoResult Pending(IReadOnlyList<string> names) => new(true, false, names);

    public static AddVideoResult Relocated(IReadOnlyList<string> names) => new(false, true, names);
}

/// <summary>Vídeo que já pertence a outra coleção. A administração precisa confirmar a mudança.</summary>
/// <param name="VideoId">Vídeo.</param>
/// <param name="Title">Título, para o aviso dizer de qual vídeo se trata.</param>
/// <param name="CollectionNames">Nomes das outras coleções, na ordem em que o aviso os mostra.</param>
public sealed record ConflitoDeVideo(Guid VideoId, string Title, string CollectionNames);

/// <summary>O que fazer com os vídeos ao excluir a coleção de verdade.</summary>
public enum VideosDaColecao
{
    /// <summary>Os vídeos saem da coleção e continuam no acervo.</summary>
    Desvincular,

    /// <summary>Os vídeos são excluídos de verdade, junto com os arquivos e os acessos.</summary>
    Excluir,

    /// <summary>Os vídeos passam para outra coleção.</summary>
    Mover
}

/// <summary>Vídeo apagado junto com a coleção. O título vai inteiro para a auditoria.</summary>
/// <param name="Id">Vídeo.</param>
/// <param name="Title">Título no momento da exclusão.</param>
public sealed record VideoExcluido(Guid Id, string Title);

/// <summary>Coleção que acabou de ser apagada, para a auditoria e a capa no storage.</summary>
/// <param name="Name">Nome, no registro do que aconteceu.</param>
/// <param name="Videos">O que foi feito com os vídeos.</param>
/// <param name="DestinationName">Coleção que recebeu os vídeos, quando eles foram movidos.</param>
/// <param name="DeletedVideos">Vídeos excluídos junto com a coleção, na ordem da playlist.</param>
public sealed record ExclusaoDeColecao(
    string Name,
    VideosDaColecao Videos,
    string? DestinationName,
    IReadOnlyList<VideoExcluido> DeletedVideos);

/// <summary>
/// Administração das coleções. Elas existem para que a concessão recaia sobre um conjunto, e
/// por isso remover um vídeo daqui muda quem consegue assisti-lo.
/// </summary>
public class CollectionService(
    OpenTubeDbContext db, TimeProvider clock, IStorageWriter storage, ILogger<CollectionService> logger)
{
    public async Task<Collection> CreateAsync(string name, string? description, Guid adminId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var colecao = Collection.Create(name, await GerarEnderecoAsync(name, cancellationToken), adminId, clock.GetUtcNow(), description);

        db.Collections.Add(colecao);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Coleção {ColecaoId} criada", colecao.Id);

        return colecao;
    }

    public Task<Collection?> FindAsync(Guid collectionId, CancellationToken cancellationToken = default) =>
        db.Collections.Include(c => c.Videos).FirstOrDefaultAsync(c => c.Id == collectionId, cancellationToken);

    public async Task<Collection> RenameAsync(Guid collectionId, string name, string? description, CancellationToken cancellationToken = default)
    {
        var colecao = await CarregarAsync(collectionId, cancellationToken);

        // O endereço não acompanha o nome: link já compartilhado não pode parar de funcionar.
        colecao.Rename(name, description);
        await db.SaveChangesAsync(cancellationToken);

        return colecao;
    }

    /// <summary>
    /// Redefine o conteúdo da coleção na ordem informada. Um vídeo que esteja em outra coleção
    /// sai de lá: cada vídeo fica em uma coleção só.
    /// </summary>
    public async Task<Collection> SetVideosAsync(Guid collectionId, IEnumerable<Guid> videoIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(videoIds);

        var colecao = await CarregarAsync(collectionId, cancellationToken);
        var existentes = await FiltrarExistentesAsync(videoIds, cancellationToken);

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);
        await SoltarDeOutrasAsync(collectionId, existentes, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        colecao.Replace(existentes, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await transacao.CommitAsync(cancellationToken);

        logger.LogInformation("Coleção {ColecaoId} agora tem {Quantidade} vídeos", collectionId, existentes.Count);

        return colecao;
    }

    /// <summary>
    /// Coloca o vídeo nesta coleção. Se ele já está em outra, não mexe em nada até
    /// <paramref name="confirmMove"/>: aí ele sai da anterior e passa a ser chegada nova aqui.
    /// </summary>
    public async Task<AddVideoResult> AddVideoAsync(
        Guid collectionId, Guid videoId, bool confirmMove = false, CancellationToken cancellationToken = default)
    {
        var colecao = await CarregarAsync(collectionId, cancellationToken);

        if (!await db.Videos.AnyAsync(v => v.Id == videoId && v.DeletedAt == null, cancellationToken))
            throw new InvalidOperationException("Video not found");

        if (colecao.Videos.Any(v => v.VideoId == videoId))
            return AddVideoResult.Added;

        var outras = await NomesDasOutrasAsync(collectionId, videoId, cancellationToken);

        if (outras.Count > 0 && !confirmMove)
            return AddVideoResult.Pending(outras);

        if (outras.Count > 0)
        {
            await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);
            await SoltarDeOutrasAsync(collectionId, [videoId], cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            colecao.Add(videoId, clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            await transacao.CommitAsync(cancellationToken);

            return AddVideoResult.Relocated(outras);
        }

        colecao.Add(videoId, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);

        return AddVideoResult.Added;
    }

    /// <summary>
    /// Outra coleção que ainda tem este vídeo. Nulo quando ele está livre ou já está nesta.
    /// </summary>
    public async Task<ConflitoDeVideo?> FindPlacementConflictAsync(
        Guid collectionId, Guid videoId, CancellationToken cancellationToken = default)
    {
        var titulo = await db.Videos
            .Where(v => v.Id == videoId && v.DeletedAt == null)
            .Select(v => v.Title)
            .FirstOrDefaultAsync(cancellationToken);

        if (titulo is null)
            return null;

        var nomes = await NomesDasOutrasAsync(collectionId, videoId, cancellationToken);

        return nomes.Count == 0 ? null : new ConflitoDeVideo(videoId, titulo, string.Join(", ", nomes));
    }

    public async Task RemoveVideoAsync(Guid collectionId, Guid videoId, CancellationToken cancellationToken = default)
    {
        var colecao = await CarregarAsync(collectionId, cancellationToken);

        colecao.Remove(videoId);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Apaga a coleção de verdade: a linha, a capa, os favoritos, os acessos e os convites
    /// sobre ela. Não há o que restaurar. Os vídeos seguem <paramref name="videos"/>.
    /// </summary>
    public async Task<ExclusaoDeColecao> DeleteAsync(
        Guid collectionId,
        VideosDaColecao videos,
        Guid? destinoId,
        CancellationToken cancellationToken = default)
    {
        var colecao = await CarregarAsync(collectionId, cancellationToken);
        var ids = colecao.Videos.OrderBy(v => v.Position).Select(v => v.VideoId).ToList();
        string? destinoNome = null;
        var excluidos = new List<VideoExcluido>();

        Collection? destino = null;
        if (videos == VideosDaColecao.Mover && ids.Count > 0)
        {
            if (destinoId is null || destinoId == collectionId)
                throw new InvalidOperationException("Choose another collection.");

            destino = await db.Collections
                .Include(c => c.Videos)
                .FirstOrDefaultAsync(c => c.Id == destinoId && c.DeletedAt == null, cancellationToken)
                ?? throw new InvalidOperationException("Choose another collection.");

            destinoNome = destino.Name;
        }

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);

        if (destino is not null)
        {
            foreach (var videoId in ids)
                colecao.Remove(videoId);

            await db.SaveChangesAsync(cancellationToken);

            var agora = clock.GetUtcNow();
            foreach (var videoId in ids)
                destino.Add(videoId, agora);

            await db.SaveChangesAsync(cancellationToken);
        }
        else if (videos == VideosDaColecao.Excluir && ids.Count > 0)
        {
            // O título precisa ser lido agora: depois do apagamento a linha do vídeo não existe mais.
            var titulos = await db.Videos.AsNoTracking()
                .Where(v => ids.Contains(v.Id))
                .Select(v => new { v.Id, v.Title })
                .ToDictionaryAsync(v => v.Id, v => v.Title, cancellationToken);

            // Os vínculos estão rastreados. Apagá-los aqui e de novo ao remover a coleção
            // faria o segundo delete esperar uma linha que já não existe.
            excluidos.AddRange(ids.Select(id => new VideoExcluido(id, titulos[id])));
        }

        await db.AccessGrants
            .Where(g => g.TargetType == GrantTargetType.Collection && g.TargetId == collectionId)
            .ExecuteDeleteAsync(cancellationToken);

        await db.Invitations
            .Where(i => i.TargetType == GrantTargetType.Collection && i.TargetId == collectionId)
            .ExecuteDeleteAsync(cancellationToken);

        var capa = colecao.ThumbnailKey;
        var nome = colecao.Name;

        db.Collections.Remove(colecao);
        await db.SaveChangesAsync(cancellationToken);

        if (excluidos.Count > 0)
            await ExclusaoPermanenteDeVideo.ApagarRegistrosAsync(db, excluidos.Select(v => v.Id).ToArray(), cancellationToken);

        await transacao.CommitAsync(cancellationToken);

        if (excluidos.Count > 0)
            await ExclusaoPermanenteDeVideo.ApagarArquivosAsync(storage, excluidos.Select(v => v.Id), logger, cancellationToken);

        if (capa is not null)
        {
            try
            {
                await storage.DeleteKeysAsync(StorageBucket.Vod, [capa], cancellationToken);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Não foi possível apagar a miniatura {Chave} da coleção excluída", capa);
            }
        }

        logger.LogInformation("Coleção {ColecaoId} excluída ({Videos})", collectionId, videos);

        return new ExclusaoDeColecao(nome, videos, destinoNome, excluidos);
    }

    /// <summary>Coleções com as contagens usadas na listagem administrativa.</summary>
    public async Task<IReadOnlyList<CollectionSummary>> ListAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        var consulta = db.Collections.AsNoTracking();

        if (!includeDeleted)
            consulta = consulta.Where(c => c.DeletedAt == null);

        return await consulta
            .OrderBy(c => c.Name)
            .Select(c => new CollectionSummary(
                c.Id,
                c.Name,
                c.Slug,
                c.Description,
                db.CollectionVideos.Count(cv => cv.CollectionId == c.Id),
                db.AccessGrants.Count(g => g.TargetId == c.Id && g.RevokedAt == null),
                c.DeletedAt != null))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Vídeos de uma coleção, na ordem definida pelo administrador.</summary>
    public async Task<IReadOnlyList<VideoSummary>> VideosOfAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        var itens = await db.CollectionVideos
            .AsNoTracking()
            .Where(cv => cv.CollectionId == collectionId)
            .OrderBy(cv => cv.Position)
            .Join(db.Videos.AsNoTracking().Where(v => v.DeletedAt == null),
                cv => cv.VideoId,
                v => v.Id,
                (cv, v) => new { cv.Position, Video = v })
            .OrderBy(x => x.Position)
            .Select(x => new
            {
                x.Video.Id,
                x.Video.Slug,
                x.Video.Title,
                x.Video.Description,
                x.Video.DurationSeconds,
                x.Video.Visibility,
                x.Video.Status,
                x.Video.PublishedAt,
                x.Video.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return itens
            .Select(x => new VideoSummary(
                x.Id, x.Slug, x.Title, x.Description, x.DurationSeconds,
                (int)x.Visibility, (int)x.Status, x.PublishedAt, x.CreatedAt, []))
            .ToList();
    }

    private async Task SoltarDeOutrasAsync(
        Guid collectionId, List<Guid> videoIds, CancellationToken cancellationToken)
    {
        if (videoIds.Count == 0)
            return;

        var outrasIds = await db.CollectionVideos
            .Where(cv => cv.CollectionId != collectionId && videoIds.Contains(cv.VideoId))
            .Select(cv => cv.CollectionId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (outrasIds.Count == 0)
            return;

        var outras = await db.Collections
            .Include(c => c.Videos)
            .Where(c => outrasIds.Contains(c.Id))
            .ToListAsync(cancellationToken);

        foreach (var outra in outras)
        {
            foreach (var videoId in videoIds)
                outra.Remove(videoId);
        }
    }

    private async Task<IReadOnlyList<string>> NomesDasOutrasAsync(
        Guid collectionId, Guid videoId, CancellationToken cancellationToken) =>
        await db.CollectionVideos
            .Where(cv => cv.VideoId == videoId && cv.CollectionId != collectionId)
            .Join(db.Collections, cv => cv.CollectionId, c => c.Id, (cv, c) => c)
            .OrderBy(c => c.Name)
            .ThenBy(c => c.Id)
            .Select(c => c.Name)
            .ToListAsync(cancellationToken);

    private async Task<List<Guid>> FiltrarExistentesAsync(IEnumerable<Guid> videoIds, CancellationToken cancellationToken)
    {
        var pedidos = videoIds.Distinct().ToList();

        var existentes = await db.Videos
            .Where(v => pedidos.Contains(v.Id) && v.DeletedAt == null)
            .Select(v => v.Id)
            .ToListAsync(cancellationToken);

        // Preserva a ordem pedida pelo administrador, descartando o que não existe mais.
        return pedidos.Where(existentes.Contains).ToList();
    }

    private async Task<Collection> CarregarAsync(Guid collectionId, CancellationToken cancellationToken) =>
        await db.Collections.Include(c => c.Videos).FirstOrDefaultAsync(c => c.Id == collectionId, cancellationToken)
        ?? throw new InvalidOperationException("Collection not found.");

    private async Task<string> GerarEnderecoAsync(string name, CancellationToken cancellationToken)
    {
        var baseSlug = Slug.From(name);
        if (baseSlug.Length == 0)
            baseSlug = "colecao";

        var ocupados = await db.Collections
            .Where(c => c.Slug == baseSlug || c.Slug.StartsWith(baseSlug + "-"))
            .Select(c => c.Slug)
            .ToListAsync(cancellationToken);

        var conjunto = ocupados.ToHashSet(StringComparer.Ordinal);

        return Slug.Unique(name, conjunto.Contains);
    }
}
