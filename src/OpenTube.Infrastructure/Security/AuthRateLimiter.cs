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
/// Limita o envio de códigos numa janela de dez minutos: por email, para ninguém trocar de
/// código o tempo todo, e por origem, com teto folgado, para ninguém disparar emails em massa
/// para a lista inteira de convidados. A digitação errada é limitada no próprio código e no
/// teto diário do email.
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

    private const int TetoPorOrigemPadrao = 50;

    private readonly SecurityOptions _options = options.Value;

    public async Task<RateLimitResult> CheckAsync(EmailAddress email, string? ipHash, CancellationToken cancellationToken = default)
    {
        var agora = clock.GetUtcNow();

        var porEmail = await VerificarAsync(EscopoEmail(email), Teto, "email", agora, cancellationToken);
        if (!porEmail.Allowed || ipHash is null)
            return porEmail;

        // A origem tem teto bem mais alto que o email: uma empresa inteira entra pelo mesmo
        // endereço, e ela não pode ficar de fora numa manhã de treinamento.
        return await VerificarAsync(EscopoOrigem(ipHash), TetoPorOrigem, "ip", agora, cancellationToken);
    }

    public async Task RecordAsync(EmailAddress email, string? ipHash, CancellationToken cancellationToken = default)
    {
        var agora = clock.GetUtcNow();

        db.AuthAttempts.Add(AuthAttempt.Record(EscopoEmail(email), agora));

        if (ipHash is not null)
            db.AuthAttempts.Add(AuthAttempt.Record(EscopoOrigem(ipHash), agora));

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<RateLimitResult> VerificarAsync(
        string escopo, int teto, string nome, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        var desde = agora - Janela;
        var ocorrencias = await db.AuthAttempts
            .Where(a => a.Scope == escopo && a.OccurredAt >= desde)
            .Select(a => a.OccurredAt)
            .ToListAsync(cancellationToken);

        if (ocorrencias.Count < teto)
            return RateLimitResult.Ok();

        var espera = ocorrencias.Min() + Janela - agora;
        if (espera < TimeSpan.Zero)
            espera = TimeSpan.Zero;

        return new RateLimitResult(false, nome, espera);
    }

    public async Task<int> PruneAsync(CancellationToken cancellationToken = default)
    {
        var corte = clock.GetUtcNow() - Janela;

        return await db.AuthAttempts
            .Where(a => a.OccurredAt < corte)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private int Teto => _options.CodesPerWindow < 1 ? TetoPadrao : _options.CodesPerWindow;

    private int TetoPorOrigem => _options.CodesPerIpWindow < 1 ? TetoPorOrigemPadrao : _options.CodesPerIpWindow;

    private static string EscopoEmail(EmailAddress email) => "email:" + email.Value;

    private static string EscopoOrigem(string ipHash) => "ip:" + ipHash;
}
