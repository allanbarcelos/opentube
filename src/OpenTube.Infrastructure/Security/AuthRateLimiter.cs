// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Entities;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Security;

/// <summary>Resultado da verificação de limite.</summary>
/// <param name="Allowed">Se o pedido pode seguir.</param>
/// <param name="Scope">Qual balde estourou, quando negado.</param>
/// <param name="RetryAfter">Quanto tempo falta para liberar.</param>
public readonly record struct RateLimitResult(bool Allowed, string? Scope, TimeSpan RetryAfter)
{
    public static RateLimitResult Ok() => new(true, null, TimeSpan.Zero);
}

/// <summary>
/// Limita o envio de códigos do mesmo email a uma janela de dez minutos. A digitação errada
/// continua limitada no próprio código.
/// </summary>
public interface IAuthRateLimiter
{
    Task<RateLimitResult> CheckAsync(EmailAddress email, string? ipHash, CancellationToken cancellationToken = default);

    Task RecordAsync(EmailAddress email, string? ipHash, CancellationToken cancellationToken = default);

    /// <summary>Descarta registros antigos demais para influenciar qualquer janela.</summary>
    Task<int> PruneAsync(CancellationToken cancellationToken = default);
}

public class AuthRateLimiter(OpenTubeDbContext db, IOptions<SecurityOptions> options, TimeProvider clock) : IAuthRateLimiter
{
    /// <summary>Única espera possível. Dez minutos depois do pedido mais antigo da janela, libera.</summary>
    public static readonly TimeSpan Janela = TimeSpan.FromMinutes(10);

    private const int TetoPadrao = 5;

    private readonly SecurityOptions _options = options.Value;

    public async Task<RateLimitResult> CheckAsync(EmailAddress email, string? ipHash, CancellationToken cancellationToken = default)
    {
        // A origem não conta. Um endereço compartilhado não pode segurar as outras pessoas.
        _ = ipHash;

        var agora = clock.GetUtcNow();
        var desde = agora - Janela;
        var ocorrencias = await db.AuthAttempts
            .Where(a => a.Scope == EscopoEmail(email) && a.OccurredAt >= desde)
            .Select(a => a.OccurredAt)
            .ToListAsync(cancellationToken);

        if (ocorrencias.Count < Teto)
            return RateLimitResult.Ok();

        var espera = ocorrencias.Min() + Janela - agora;
        if (espera < TimeSpan.Zero)
            espera = TimeSpan.Zero;

        return new RateLimitResult(false, "email", espera);
    }

    public async Task RecordAsync(EmailAddress email, string? ipHash, CancellationToken cancellationToken = default)
    {
        _ = ipHash;

        db.AuthAttempts.Add(AuthAttempt.Record(EscopoEmail(email), clock.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> PruneAsync(CancellationToken cancellationToken = default)
    {
        var corte = clock.GetUtcNow() - Janela;

        return await db.AuthAttempts
            .Where(a => a.OccurredAt < corte)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private int Teto => _options.CodesPerWindow < 1 ? TetoPadrao : _options.CodesPerWindow;

    private static string EscopoEmail(EmailAddress email) => "email:" + email.Value;
}
