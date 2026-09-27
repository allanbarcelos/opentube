// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenTube.Domain.Entities;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Persistence;
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

/// <summary>
/// Administração das coleções. Elas existem para que a concessão recaia sobre um conjunto, e
/// por isso remover um vídeo daqui muda quem consegue assisti-lo.
/// </summary>
public class CollectionService(OpenTubeDbContext db, TimeProvider clock, ILogger<CollectionService> logger)
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

    /// <summary>Redefine o conteúdo da coleção na ordem informada.</summary>
    public async Task<Collection> SetVideosAsync(Guid collectionId, IEnumerable<Guid> videoIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(videoIds);

        var colecao = await CarregarAsync(collectionId, cancellationToken);
        var existentes = await FiltrarExistentesAsync(videoIds, cancellationToken);

        colecao.Replace(existentes);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Coleção {ColecaoId} agora tem {Quantidade} vídeos", collectionId, existentes.Count);

        return colecao;
    }

    public async Task AddVideoAsync(Guid collectionId, Guid videoId, CancellationToken cancellationToken = default)
    {
        var colecao = await CarregarAsync(collectionId, cancellationToken);

        if (!await db.Videos.AnyAsync(v => v.Id == videoId && v.DeletedAt == null, cancellationToken))
            throw new InvalidOperationException("Video not found");

        colecao.Add(videoId);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveVideoAsync(Guid collectionId, Guid videoId, CancellationToken cancellationToken = default)
    {
        var colecao = await CarregarAsync(collectionId, cancellationToken);

        colecao.Remove(videoId);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        var colecao = await CarregarAsync(collectionId, cancellationToken);

        colecao.SoftDelete(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RestoreAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        var colecao = await CarregarAsync(collectionId, cancellationToken);

        colecao.Restore();
        await db.SaveChangesAsync(cancellationToken);
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
