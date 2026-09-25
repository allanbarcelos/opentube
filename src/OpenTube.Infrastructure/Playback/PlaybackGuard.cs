using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTube.Infrastructure.Analytics;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Playback;

/// <summary>
/// Limita quantas origens distintas reproduzem ao mesmo tempo com a mesma conta. A contagem é
/// por origem, e não por sessão: recarregar a página não deve consumir o limite, enquanto
/// duas pessoas em lugares diferentes devem.
/// </summary>
public class PlaybackGuard(OpenTubeDbContext db, IOptions<SecurityOptions> options, TimeProvider clock)
{
    private readonly SecurityOptions _options = options.Value;

    /// <summary>
    /// Verifica se mais uma reprodução cabe no limite. Origem desconhecida não é contada —
    /// chutar que é alguém novo puniria quem simplesmente está atrás de um proxy.
    /// </summary>
    public async Task<bool> AllowsAnotherAsync(Guid? userId, string? ipHash, CancellationToken cancellationToken = default)
    {
        if (_options.MaxConcurrentPlaybacks <= 0 || userId is not { } pessoa)
            return true;

        var corte = clock.GetUtcNow() - AnalyticsCollector.InactivityTimeout;

        var origens = await db.PlaybackSessions
            .AsNoTracking()
            .Where(s => s.UserId == pessoa && s.EndedAt == null && s.LastSeenAt >= corte && s.IpHash != null)
            .Select(s => s.IpHash!)
            .Distinct()
            .ToListAsync(cancellationToken);

        // A própria origem já contada não consome uma vaga a mais.
        if (ipHash is not null && origens.Contains(ipHash, StringComparer.Ordinal))
            return true;

        return origens.Count < _options.MaxConcurrentPlaybacks;
    }
}
