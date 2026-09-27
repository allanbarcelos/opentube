// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.AspNetCore.Mvc;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Branding;
using OpenTube.Infrastructure.Localization;
using OpenTube.Infrastructure.Security;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>
/// Marca d'água do acervo: a imagem, servida a qualquer visitante porque aparece sobre todos
/// os vídeos, e a administração dela.
/// </summary>
public static class WatermarkEndpoints
{
    public const string ImagePath = "/branding/watermark.png";

    public static IEndpointRouteBuilder MapWatermarkEndpoints(this IEndpointRouteBuilder rotas)
    {
        rotas.MapGet(ImagePath, async (
            [FromQuery(Name = "v")] long? versao,
            WatermarkService marcas,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var marca = await marcas.GetImageAsync(cancellationToken);
            if (marca is null)
                return Results.NotFound();

            // O endereço leva a versão: com ela certa, a imagem pode ficar em cache por um ano,
            // porque uma troca gera outro endereço. Sem ela, o navegador sempre confere.
            contexto.Response.Headers.CacheControl = versao == marca.Version
                ? "public, max-age=31536000, immutable"
                : "no-cache";

            return Results.File(marca.Image, PlayerWatermark.ContentType,
                entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{marca.Version}\""));
        });

        var administracao = rotas.MapGroup("/admin/watermark").RequireAuthorization(Policies.Administrator);

        // Imagem nova (com a posição) ou só a posição, quando nenhum arquivo é enviado.
        administracao.MapPost("/save", async (
            IFormFile? arquivo,
            [FromForm] int posicao,
            WatermarkService marcas,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var admin = ViewerContext.From(contexto.User);
            var lugar = (WatermarkPosition)posicao;

            try
            {
                if (arquivo is null || arquivo.Length == 0)
                {
                    await marcas.MoveAsync(lugar, admin.UserId!.Value, cancellationToken);

                    await contexto.RegistrarAsync(
                        AuditActions.MarcaReposicionada, AuditEntities.MarcaDagua, null,
                        LocalText.Format("Watermark moved to {0}", lugar), cancellationToken);

                    return Results.Redirect("/admin/watermark?salvo=1");
                }

                // O limite é conferido antes de ler: um arquivo enorme não chega à memória.
                if (arquivo.Length > WatermarkImageProcessor.MaxUploadBytes)
                    throw new ArgumentException("The image is larger than 5 MB.");

                using var memoria = new MemoryStream((int)arquivo.Length);
                await arquivo.CopyToAsync(memoria, cancellationToken);

                var marca = await marcas.SaveAsync(memoria.ToArray(), lugar, admin.UserId!.Value, cancellationToken);

                await contexto.RegistrarAsync(
                    AuditActions.MarcaDefinida, AuditEntities.MarcaDagua, null,
                    LocalText.Format("Watermark image set ({0}×{1}, {2})", marca.Width, marca.Height, marca.Position),
                    cancellationToken);

                return Results.Redirect("/admin/watermark?salvo=1");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Results.Redirect($"/admin/watermark?erro={Uri.EscapeDataString(LocalText.Get(e.Message))}");
            }
        });

        administracao.MapPost("/remove", async (
            WatermarkService marcas,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            if (await marcas.RemoveAsync(cancellationToken))
            {
                await contexto.RegistrarAsync(
                    AuditActions.MarcaRemovida, AuditEntities.MarcaDagua, null,
                    LocalText.Get("Watermark image removed"), cancellationToken);
            }

            return Results.Redirect("/admin/watermark?removido=1");
        });

        return rotas;
    }
}
