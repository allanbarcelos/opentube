using Microsoft.AspNetCore.Mvc;
using OpenTube.Infrastructure.Domains;
using OpenTube.Infrastructure.Localization;
using OpenTube.Infrastructure.Security;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>Cadastro e verificação de domínios, e a porta de entrada dedicada de cada um.</summary>
public static class DomainEndpoints
{
    public static IEndpointRouteBuilder MapDomainEndpoints(this IEndpointRouteBuilder rotas)
    {
        MapAdministracao(rotas);
        MapPortaDeEntrada(rotas);

        return rotas;
    }

    private static void MapAdministracao(IEndpointRouteBuilder rotas)
    {
        var grupo = rotas.MapGroup("/admin/domains").RequireAuthorization(Policies.Administrator);

        grupo.MapPost("/register", async (
            [FromForm] string dominio,
            [FromForm] string? responsavel,
            [FromForm] string? nota,
            DomainService dominios,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var admin = ViewerContext.From(contexto.User);

            try
            {
                var cadastrado = await dominios.RegisterAsync(dominio, admin.UserId!.Value, responsavel, nota, cancellationToken);

                await contexto.RegistrarAsync(
                    AuditActions.DominioCadastrado, AuditEntities.Dominio, cadastrado.Id,
                    LocalText.Format("Domain {0} registered", cadastrado.Name), cancellationToken);

                return Results.Redirect($"/admin/domains/{cadastrado.Id}?cadastrado=1");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Results.Redirect($"/admin/domains?erro={Uri.EscapeDataString(LocalText.Get(e.Message))}");
            }
        });

        grupo.MapPost("/{domainId:guid}/verify", async (
            Guid domainId,
            DomainService dominios,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var resultado = await dominios.VerifyAsync(domainId, cancellationToken);

            if (resultado.Verified)
            {
                await contexto.RegistrarAsync(
                    AuditActions.DominioVerificado, AuditEntities.Dominio, domainId,
                    LocalText.Get("Domain ownership proved by DNS"), cancellationToken);
            }

            return resultado.Verified
                ? Results.Redirect($"/admin/domains/{domainId}?verificado=1")
                : Results.Redirect($"/admin/domains/{domainId}?falhou=1");
        });

        grupo.MapPost("/{domainId:guid}/reissue", async (
            Guid domainId,
            DomainService dominios,
            CancellationToken cancellationToken) =>
        {
            await dominios.ResetVerificationAsync(domainId, cancellationToken);

            return Results.Redirect($"/admin/domains/{domainId}?reemitido=1");
        });

        grupo.MapPost("/{domainId:guid}/save", async (
            Guid domainId,
            [FromForm] string? endereco,
            [FromForm] string? portaAtiva,
            [FromForm] string? permitidos,
            [FromForm] string? responsavel,
            [FromForm] string? nota,
            DomainService dominios,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await dominios.UpdateAsync(
                    domainId,
                    endereco,
                    portaAtiva == "on",
                    AccessEndpoints.SepararEmails(permitidos),
                    responsavel,
                    nota,
                    cancellationToken);

                return Results.Redirect($"/admin/domains/{domainId}?salvo=1");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Results.Redirect($"/admin/domains/{domainId}?erro={Uri.EscapeDataString(LocalText.Get(e.Message))}");
            }
        });

        grupo.MapPost("/{domainId:guid}/send-link", async (
            Guid domainId,
            DomainService dominios,
            CancellationToken cancellationToken) =>
        {
            var enviado = await dominios.SendEntryLinkAsync(domainId, cancellationToken);

            return Results.Redirect($"/admin/domains/{domainId}?{(enviado ? "linkEnviado=1" : "semResponsavel=1")}");
        });
    }

    private static void MapPortaDeEntrada(IEndpointRouteBuilder rotas)
    {
        rotas.MapPost("/entry/{slug}/code", async (
            string slug,
            [FromForm] string email,
            DomainService dominios,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var resultado = await dominios.RequestEntryCodeAsync(
                slug, email, contexto.Connection.RemoteIpAddress?.ToString(), cancellationToken);

            var endereco = $"/entry/{Uri.EscapeDataString(slug)}";

            return resultado switch
            {
                DomainEntryFailure.DomainNotFound => Results.Redirect("/not-found"),
                DomainEntryFailure.InvalidEmail => Results.Redirect(
                    $"{endereco}?erro={Uri.EscapeDataString(LocalText.Get("Invalid email address."))}"),
                DomainEntryFailure.EmailNotAccepted => Results.Redirect(
                    $"{endereco}?erro={Uri.EscapeDataString(LocalText.Get("This address does not belong to the allowed domain."))}"),
                DomainEntryFailure.RateLimited => Results.Redirect(
                    $"{endereco}?erro={Uri.EscapeDataString(LocalText.Get("Too many requests. Wait a few minutes."))}"),
                _ => Results.Redirect($"{endereco}?email={Uri.EscapeDataString(email)}&enviado=1")
            };
        });
    }
}
