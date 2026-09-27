// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Access;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Access;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>
/// Entrada pelo link secreto de compartilhamento. O token vai para um cookie e é conferido a
/// cada requisição, para que a revogação tenha efeito imediato.
/// </summary>
public static class ShareEndpoints
{
    public static IEndpointRouteBuilder MapShareEndpoints(this IEndpointRouteBuilder rotas)
    {
        rotas.MapGet("/link/{token}", async (
            string token,
            AccessService acesso,
            OpenTubeDbContext db,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var espectador = await acesso.ResolveLinkAsync(Viewer.Anonymous, token, cancellationToken);

            if (espectador.LinkGrantId is not { } concessaoId)
                return Results.Redirect("/not-found");

            var concessao = await db.AccessGrants
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.Id == concessaoId, cancellationToken);

            if (concessao is null || !concessao.IsActiveAt(DateTimeOffset.UtcNow))
                return Results.Redirect("/not-found");

            contexto.Response.Cookies.Append(
                CurrentViewer.LinkCookieName,
                token,
                CurrentViewer.LinkCookieOptions(contexto.Request.IsHttps));

            // Quando o link é de um vídeo, leva direto a ele; nos demais casos, à home, que
            // já vai listar o que a concessão liberou.
            if (concessao.TargetType is GrantTargetType.Video && concessao.TargetId is { } videoId)
            {
                var endereco = await db.Videos
                    .Where(v => v.Id == videoId)
                    .Select(v => v.Slug)
                    .FirstOrDefaultAsync(cancellationToken);

                if (endereco is not null)
                    return Results.Redirect($"/watch/{endereco}");
            }

            return Results.Redirect("/");
        });

        return rotas;
    }
}
