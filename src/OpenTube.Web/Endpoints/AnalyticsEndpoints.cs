// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using OpenTube.Infrastructure.Analytics;
using OpenTube.Infrastructure.Playback;
using OpenTube.Infrastructure.Security;
using OpenTube.Shared.Analytics;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>Pedido de abertura de uma sessão de reprodução.</summary>
/// <param name="VideoId">Vídeo que será assistido.</param>
public sealed record AbrirSessao(Guid VideoId);

/// <summary>
/// Coleta do que o player relata. Estes endereços não exigem a credencial antifalsificação
/// porque o navegador envia as batidas com <c>sendBeacon</c>, que não permite cabeçalhos
/// próprios; em troca, a sessão é um identificador imprevisível e cada relato é conferido
/// contra quem a abriu.
/// </summary>
public static class AnalyticsEndpoints
{
    /// <summary>Cookie que identifica o visitante anônimo entre sessões.</summary>
    public const string VisitorCookieName = "opentube.visitante";

    /// <summary>
    /// Teto de eventos por lote. O player envia a cada dez segundos e acumula poucos eventos
    /// nesse intervalo; um lote maior só pode ser alguém tentando inflar a tabela de eventos.
    /// </summary>
    public const int MaxEventsPerBatch = 100;

    private const string PoliticaDeLimite = "reproducao";

    /// <summary>
    /// Limite de pedidos por origem. Os endereços aceitam visitantes anônimos em vídeo
    /// público, então sem limite qualquer um encheria o banco de sessões e eventos. O teto é
    /// folgado para caber uma sala inteira assistindo atrás do mesmo endereço.
    /// </summary>
    public static void AddRateLimit(RateLimiterOptions opcoes) =>
        opcoes.AddPolicy(PoliticaDeLimite, contexto =>
            contexto.Connection.RemoteIpAddress is { } origem
                ? RateLimitPartition.GetFixedWindowLimiter(origem.ToString(), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 600,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                })
                : RateLimitPartition.GetNoLimiter("sem-origem"));

    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder rotas)
    {
        var grupo = rotas.MapGroup("/api/playback").RequireRateLimiting(PoliticaDeLimite);

        grupo.MapPost("/start", async (
            [FromBody] AbrirSessao pedido,
            AnalyticsCollector coletor,
            PlaybackService playback,
            CurrentViewer espectadores,
            PrivacyHasher privacidade,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var espectador = await espectadores.GetAsync(cancellationToken);

            // Só registra quem de fato pode assistir: sem esta conferência, qualquer um
            // conseguiria criar sessões para vídeos que não tem permissão de ver.
            if (!await playback.CanWatchAsync(pedido.VideoId, espectador, cancellationToken))
                return Results.NotFound();

            var visitante = GarantirVisitante(contexto);

            var sessao = await coletor.StartAsync(
                pedido.VideoId,
                espectador.UserId,
                espectador.IsAuthenticated ? null : visitante,
                espectador.LinkGrantId,
                contexto.Request.Headers.UserAgent.ToString(),
                privacidade.HashIp(contexto.Connection.RemoteIpAddress?.ToString()),
                contexto.Request.Headers.Referer.ToString(),
                cancellationToken);

            return Results.Ok(new { sessaoId = sessao.Id });
        });

        grupo.MapPost("/{sessionId:guid}/events", async (
            Guid sessionId,
            [FromBody] PlaybackBatch lote,
            AnalyticsCollector coletor,
            CurrentViewer espectadores,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            if (lote.Eventos?.Length > MaxEventsPerBatch)
                return Results.BadRequest();

            var espectador = await espectadores.GetAsync(cancellationToken);
            var visitante = contexto.Request.Cookies[VisitorCookieName];

            var aceito = await coletor.RecordAsync(
                sessionId, espectador.UserId, visitante, lote, cancellationToken);

            return aceito ? Results.NoContent() : Results.NotFound();
        });

        grupo.MapPost("/{sessionId:guid}/close", async (
            Guid sessionId,
            AnalyticsCollector coletor,
            CurrentViewer espectadores,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var espectador = await espectadores.GetAsync(cancellationToken);

            await coletor.EndAsync(
                sessionId, espectador.UserId, contexto.Request.Cookies[VisitorCookieName], cancellationToken);

            return Results.NoContent();
        });

        return rotas;
    }

    /// <summary>
    /// Identificador do visitante anônimo, guardado no navegador dele. Serve apenas para
    /// distinguir uma pessoa de outra no relatório; não identifica ninguém.
    /// </summary>
    private static string GarantirVisitante(HttpContext contexto)
    {
        if (contexto.Request.Cookies.TryGetValue(VisitorCookieName, out var existente) &&
            !string.IsNullOrWhiteSpace(existente))
            return existente;

        var novo = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        contexto.Response.Cookies.Append(VisitorCookieName, novo, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = contexto.Request.IsHttps,
            MaxAge = TimeSpan.FromDays(365),
            IsEssential = true,
            Path = "/"
        });

        return novo;
    }
}
