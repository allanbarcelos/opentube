using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Branding;

/// <summary>O que a página do vídeo precisa saber da marca, sem carregar a imagem.</summary>
/// <param name="Position">Onde a marca fica sobre o vídeo.</param>
/// <param name="Width">Largura da imagem, em pixels.</param>
/// <param name="Height">Altura da imagem, em pixels.</param>
/// <param name="Version">Muda a cada troca; vai no endereço da imagem.</param>
public sealed record WatermarkInfo(WatermarkPosition Position, int Width, int Height, long Version);

/// <summary>Imagem da marca, pronta para ser servida.</summary>
/// <param name="Image">Bytes do PNG.</param>
/// <param name="Version">Versão da imagem.</param>
public sealed record WatermarkImage(byte[] Image, long Version);

/// <summary>
/// Marca d'água do acervo, definida pela administração e exibida sobre todos os vídeos.
/// </summary>
public class WatermarkService(OpenTubeDbContext db, TimeProvider clock)
{
    public async Task<WatermarkInfo?> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var marca = await db.PlayerWatermarks
            .AsNoTracking()
            .Where(m => m.Id == PlayerWatermark.SingletonId)
            .Select(m => new { m.Position, m.Width, m.Height, m.UpdatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        return marca is null
            ? null
            : new WatermarkInfo(marca.Position, marca.Width, marca.Height, marca.UpdatedAt.ToUnixTimeMilliseconds());
    }

    public async Task<WatermarkImage?> GetImageAsync(CancellationToken cancellationToken = default)
    {
        var marca = await db.PlayerWatermarks
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == PlayerWatermark.SingletonId, cancellationToken);

        return marca is null ? null : new WatermarkImage(marca.Image, marca.Version);
    }

    /// <summary>
    /// Define a marca ou troca a imagem da que já existe. A imagem é levada ao tamanho padrão
    /// antes de guardada; o que chega ao banco e aos navegadores é sempre essa versão.
    /// </summary>
    public async Task<PlayerWatermark> SaveAsync(
        byte[] image, WatermarkPosition position, Guid adminId, CancellationToken cancellationToken = default)
    {
        image = WatermarkImageProcessor.Normalize(image);
        var agora = clock.GetUtcNow();
        var marca = await db.PlayerWatermarks.FirstOrDefaultAsync(m => m.Id == PlayerWatermark.SingletonId, cancellationToken);

        if (marca is null)
        {
            marca = PlayerWatermark.Define(image, position, adminId, agora);
            db.PlayerWatermarks.Add(marca);
        }
        else
        {
            marca.Replace(image, position, adminId, agora);
        }

        await db.SaveChangesAsync(cancellationToken);

        return marca;
    }

    /// <summary>Muda só a posição da marca existente.</summary>
    public async Task<PlayerWatermark> MoveAsync(
        WatermarkPosition position, Guid adminId, CancellationToken cancellationToken = default)
    {
        var marca = await db.PlayerWatermarks.FirstOrDefaultAsync(m => m.Id == PlayerWatermark.SingletonId, cancellationToken)
            ?? throw new InvalidOperationException("There is no watermark image yet.");

        marca.MoveTo(position, adminId, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);

        return marca;
    }

    /// <summary>Remove a marca. Devolve <c>false</c> quando não havia nenhuma.</summary>
    public async Task<bool> RemoveAsync(CancellationToken cancellationToken = default) =>
        await db.PlayerWatermarks
            .Where(m => m.Id == PlayerWatermark.SingletonId)
            .ExecuteDeleteAsync(cancellationToken) > 0;
}
