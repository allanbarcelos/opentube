// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Access;
using OpenTube.Infrastructure.Branding;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Playback;
using OpenTube.Infrastructure.Storage;

namespace OpenTube.Infrastructure.Services;

/// <summary>
/// Miniatura opcional da coleção. Sem imagem enviada, o cartão continua mostrando o nome.
/// </summary>
public class CollectionThumbnailService(
    OpenTubeDbContext db,
    IVideoStorage storage,
    VideoCatalog catalogo,
    TimeProvider clock,
    IOptions<StorageOptions> storageOptions,
    ILogger<CollectionThumbnailService> logger)
{
    /// <summary>Grava a imagem já normalizada e aponta a coleção para ela.</summary>
    public async Task SetAsync(Guid collectionId, byte[] image, CancellationToken cancellationToken = default)
    {
        var jpeg = CollectionThumbnailProcessor.Normalize(image);
        var colecao = await CarregarAsync(collectionId, cancellationToken);

        var versao = clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (versao <= colecao.ThumbnailVersion)
            versao = colecao.ThumbnailVersion + 1;

        var chave = StorageKeys.CollectionThumbnail(colecao.Id, versao);
        var anterior = colecao.ThumbnailKey;

        await storage.PutBytesAsync(StorageBucket.Vod, chave, jpeg, MediaTypes.Jpeg, cancellationToken);

        colecao.SetThumbnail(chave, versao);
        await db.SaveChangesAsync(cancellationToken);

        if (anterior is not null)
            await ApagarAsync(anterior, cancellationToken);

        logger.LogInformation("Coleção {ColecaoId} recebeu miniatura {Chave}", collectionId, chave);
    }

    /// <summary>Remove a imagem e volta à capa com o nome. Devolve falso quando não havia imagem.</summary>
    public async Task<bool> RemoveAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        var colecao = await CarregarAsync(collectionId, cancellationToken);
        var anterior = colecao.ThumbnailKey;

        if (anterior is null)
            return false;

        colecao.ClearThumbnail();
        await db.SaveChangesAsync(cancellationToken);
        await ApagarAsync(anterior, cancellationToken);

        return true;
    }

    /// <summary>
    /// URL assinada da miniatura, ou nulo quando não há imagem ou quem pede não vê a coleção.
    /// Administrador vê mesmo a coleção excluída, para a página de administração continuar
    /// mostrando a capa.
    /// </summary>
    public async Task<string?> GetUrlAsync(Guid collectionId, Viewer viewer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        var colecao = await db.Collections
            .AsNoTracking()
            .Where(c => c.Id == collectionId)
            .Select(c => new { c.ThumbnailKey, c.DeletedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (colecao?.ThumbnailKey is null)
            return null;

        if (!await LiberadaAsync(collectionId, colecao.DeletedAt, viewer, cancellationToken))
            return null;

        return storage.SignDownloadUrl(StorageBucket.Vod, colecao.ThumbnailKey, storageOptions.Value.PlaybackUrlLifetime);
    }

    /// <summary>
    /// O servidor da frente pergunta isto antes de entregar a capa. Só a imagem atual da
    /// coleção, e só para quem já pode vê-la. Administrador vê mesmo a coleção excluída.
    /// </summary>
    public async Task<bool> PodeEntregarAsync(string chave, Viewer viewer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentException.ThrowIfNullOrWhiteSpace(chave);

        var colecao = await db.Collections
            .AsNoTracking()
            .Where(c => c.ThumbnailKey == chave)
            .Select(c => new { c.Id, c.DeletedAt })
            .FirstOrDefaultAsync(cancellationToken);

        return colecao is not null
            && await LiberadaAsync(colecao.Id, colecao.DeletedAt, viewer, cancellationToken);
    }

    /// <summary>Mesma regra da URL assinada e da autorização por pedido.</summary>
    private async Task<bool> LiberadaAsync(
        Guid collectionId, DateTimeOffset? deletedAt, Viewer viewer, CancellationToken cancellationToken)
    {
        if (viewer.IsAdmin)
            return true;

        if (deletedAt is not null)
            return false;

        return await catalogo.CollectionIsVisibleAsync(viewer, collectionId, cancellationToken);
    }

    private async Task<Domain.Entities.Collection> CarregarAsync(Guid collectionId, CancellationToken cancellationToken) =>
        await db.Collections.FirstOrDefaultAsync(c => c.Id == collectionId, cancellationToken)
        ?? throw new InvalidOperationException("Collection not found.");

    /// <summary>A capa nova já está no ar; falhar ao apagar a antiga não desfaz a troca.</summary>
    private async Task ApagarAsync(string chave, CancellationToken cancellationToken)
    {
        try
        {
            await storage.DeleteKeysAsync(StorageBucket.Vod, [chave], cancellationToken);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Não foi possível apagar a miniatura anterior {Chave}", chave);
        }
    }
}
