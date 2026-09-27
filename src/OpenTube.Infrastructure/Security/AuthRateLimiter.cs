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
/// Limita o envio e a conferência de códigos. Como o sistema não tem senha, este é o único
/// obstáculo real entre um atacante e um código de seis dígitos.
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
    private static readonly TimeSpan JanelaIp = TimeSpan.FromHours(1);
    private static readonly TimeSpan JanelaDiaria = TimeSpan.FromDays(1);

    private readonly SecurityOptions _options = options.Value;

    public async Task<RateLimitResult> CheckAsync(EmailAddress email, string? ipHash, CancellationToken cancellationToken = default)
    {
        var agora = clock.GetUtcNow();

        if (!string.IsNullOrWhiteSpace(ipHash))
        {
            var porIp = await ContarAsync(EscopoIp(ipHash), agora - JanelaIp, cancellationToken);
            if (porIp >= _options.CodesPerHourPerIp)
                return new RateLimitResult(false, "origem", JanelaIp);
        }

        var porEmail = await ContarAsync(EscopoEmail(email), agora - JanelaDiaria, cancellationToken);
        if (porEmail >= _options.CodesPerDayPerEmail)
            return new RateLimitResult(false, "email", JanelaDiaria);

        var porDominio = await ContarAsync(EscopoDominio(email), agora - JanelaDiaria, cancellationToken);
        if (porDominio >= _options.CodesPerDayPerDomain)
            return new RateLimitResult(false, "domínio", JanelaDiaria);

        return RateLimitResult.Ok();
    }

    public async Task RecordAsync(EmailAddress email, string? ipHash, CancellationToken cancellationToken = default)
    {
        var agora = clock.GetUtcNow();

        db.AuthAttempts.Add(AuthAttempt.Record(EscopoEmail(email), agora));
        db.AuthAttempts.Add(AuthAttempt.Record(EscopoDominio(email), agora));

        if (!string.IsNullOrWhiteSpace(ipHash))
            db.AuthAttempts.Add(AuthAttempt.Record(EscopoIp(ipHash), agora));

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> PruneAsync(CancellationToken cancellationToken = default)
    {
        var corte = clock.GetUtcNow() - JanelaDiaria - TimeSpan.FromHours(1);

        return await db.AuthAttempts
            .Where(a => a.OccurredAt < corte)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private Task<int> ContarAsync(string escopo, DateTimeOffset desde, CancellationToken cancellationToken) =>
        db.AuthAttempts.CountAsync(a => a.Scope == escopo && a.OccurredAt >= desde, cancellationToken);

    private static string EscopoIp(string ipHash) => "ip:" + ipHash;

    private static string EscopoEmail(EmailAddress email) => "email:" + email.Value;

    private static string EscopoDominio(EmailAddress email) => "dominio:" + email.Domain;
}
