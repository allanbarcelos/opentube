using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Entities;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Security;

/// <summary>
/// Garante que os endereços listados na configuração tenham o papel de administrador. É como
/// o primeiro acesso nasce: sem senha inicial e sem conta embutida no código.
/// </summary>
public class AdminSeeder(
    OpenTubeDbContext db,
    IOptions<SecurityOptions> options,
    TimeProvider clock,
    ILogger<AdminSeeder> logger)
{
    private readonly SecurityOptions _options = options.Value;

    public async Task<int> EnsureAdminsAsync(CancellationToken cancellationToken = default)
    {
        var enderecos = _options.AdminEmails
            .Select(e => EmailAddress.TryParse(e, out var endereco) ? endereco : (EmailAddress?)null)
            .Where(e => e is not null)
            .Select(e => e!.Value)
            .DistinctBy(e => e.Value)
            .ToList();

        if (enderecos.Count == 0)
        {
            logger.LogWarning("Nenhum administrador configurado; ninguém consegue entrar na área administrativa.");
            return 0;
        }

        var agora = clock.GetUtcNow();
        var alterados = 0;

        foreach (var endereco in enderecos)
        {
            var usuario = await db.Users.FirstOrDefaultAsync(u => u.Email == endereco.Value, cancellationToken);

            if (usuario is null)
            {
                db.Users.Add(User.Create(endereco, agora, isAdmin: true));
                alterados++;
                logger.LogInformation("Administrador {Email} criado", endereco.Value);
                continue;
            }

            if (usuario.IsAdmin)
                continue;

            usuario.Enable();
            usuario.GrantAdmin();
            alterados++;
            logger.LogInformation("Administrador {Email} promovido", endereco.Value);
        }

        if (alterados > 0)
            await db.SaveChangesAsync(cancellationToken);

        return alterados;
    }
}
