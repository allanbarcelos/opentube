// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.AspNetCore.Mvc;
using OpenTube.Domain.Entities;
using OpenTube.Infrastructure.Branding;
using OpenTube.Infrastructure.Localization;
using OpenTube.Infrastructure.Security;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>
/// Personalização do site: o logotipo, servido a qualquer visitante porque aparece na barra de
/// todas as páginas, e a administração do nome, do logotipo e do rodapé.
/// </summary>
public static class SiteBrandingEndpoints
{
    public const string LogoPath = "/branding/logo.png";

    public const string AdminPath = "/admin/customization";

    /// <summary>Endereço do logotipo com a versão: uma troca gera outro endereço.</summary>
    public static string LogoUrl(SiteIdentity site)
    {
        ArgumentNullException.ThrowIfNull(site);

        return $"{LogoPath}?v={site.LogoVersion}";
    }

    public static IEndpointRouteBuilder MapSiteBrandingEndpoints(this IEndpointRouteBuilder rotas)
    {
        rotas.MapGet(LogoPath, async (
            [FromQuery(Name = "v")] long? versao,
            SiteBrandingService personalizacao,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var logo = await personalizacao.GetLogoAsync(cancellationToken);
            if (logo is null)
                return Results.NotFound();

            // Com a versão certa no endereço, a imagem fica em cache por um ano; sem ela, o
            // navegador sempre confere.
            contexto.Response.Headers.CacheControl = versao == logo.Version
                ? "public, max-age=31536000, immutable"
                : "no-cache";

            return Results.File(logo.Image, SiteBranding.LogoContentType,
                entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{logo.Version}\""));
        });

        var administracao = rotas.MapGroup(AdminPath).RequireAuthorization(Policies.Administrator);

        // Nome e rodapé sempre; o logotipo só quando um arquivo vem junto.
        administracao.MapPost("/save", async (
            IFormFile? logo,
            [FromForm] string? nome,
            [FromForm] bool? credito,
            [FromForm] bool? repositorio,
            SiteBrandingService personalizacao,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var admin = ViewerContext.From(contexto.User);

            try
            {
                byte[]? imagem = null;

                if (logo is { Length: > 0 })
                {
                    // O limite é conferido antes de ler: um arquivo enorme não chega à memória.
                    if (logo.Length > WatermarkImageProcessor.MaxUploadBytes)
                        throw new ArgumentException("The image is larger than 5 MB.");

                    using var memoria = new MemoryStream((int)logo.Length);
                    await logo.CopyToAsync(memoria, cancellationToken);
                    imagem = memoria.ToArray();
                }

                var salvo = await personalizacao.SaveAsync(
                    nome ?? string.Empty, credito == true, repositorio == true, imagem, admin.UserId!.Value, cancellationToken);

                await contexto.RegistrarAsync(
                    AuditActions.PersonalizacaoAlterada, AuditEntities.Personalizacao, null,
                    LocalText.Format(
                        imagem is null ? "Site customization saved: “{0}”" : "Site customization saved, with a new logo: “{0}”",
                        salvo.Name),
                    cancellationToken);

                return Results.Redirect(AdminPath + "?salvo=1");
            }
            catch (ArgumentException e)
            {
                return Results.Redirect($"{AdminPath}?erro={Uri.EscapeDataString(LocalText.Get(e.Message))}");
            }
        });

        administracao.MapPost("/remove-logo", async (
            SiteBrandingService personalizacao,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var admin = ViewerContext.From(contexto.User);

            if (await personalizacao.RemoveLogoAsync(admin.UserId!.Value, cancellationToken))
            {
                await contexto.RegistrarAsync(
                    AuditActions.LogoRemovido, AuditEntities.Personalizacao, null,
                    LocalText.Get("Site logo removed"), cancellationToken);
            }

            return Results.Redirect(AdminPath + "?logo-removido=1");
        });

        return rotas;
    }
}
