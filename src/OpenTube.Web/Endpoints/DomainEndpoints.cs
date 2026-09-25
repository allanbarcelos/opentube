using Microsoft.AspNetCore.Mvc;
using OpenTube.Infrastructure.Domains;
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
        var grupo = rotas.MapGroup("/admin/dominios").RequireAuthorization(Policies.Administrator);

        grupo.MapPost("/cadastrar", async (
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

                return Results.Redirect($"/admin/dominios/{cadastrado.Id}?cadastrado=1");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Results.Redirect($"/admin/dominios?erro={Uri.EscapeDataString(e.Message)}");
            }
        });

        grupo.MapPost("/{domainId:guid}/verificar", async (
            Guid domainId,
            DomainService dominios,
            CancellationToken cancellationToken) =>
        {
            var resultado = await dominios.VerifyAsync(domainId, cancellationToken);

            return resultado.Verified
                ? Results.Redirect($"/admin/dominios/{domainId}?verificado=1")
                : Results.Redirect($"/admin/dominios/{domainId}?falhou=1");
        });

        grupo.MapPost("/{domainId:guid}/reemitir", async (
            Guid domainId,
            DomainService dominios,
            CancellationToken cancellationToken) =>
        {
            await dominios.ResetVerificationAsync(domainId, cancellationToken);

            return Results.Redirect($"/admin/dominios/{domainId}?reemitido=1");
        });

        grupo.MapPost("/{domainId:guid}/salvar", async (
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

                return Results.Redirect($"/admin/dominios/{domainId}?salvo=1");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Results.Redirect($"/admin/dominios/{domainId}?erro={Uri.EscapeDataString(e.Message)}");
            }
        });

        grupo.MapPost("/{domainId:guid}/enviar-link", async (
            Guid domainId,
            DomainService dominios,
            CancellationToken cancellationToken) =>
        {
            var enviado = await dominios.SendEntryLinkAsync(domainId, cancellationToken);

            return Results.Redirect($"/admin/dominios/{domainId}?{(enviado ? "linkEnviado=1" : "semResponsavel=1")}");
        });
    }

    private static void MapPortaDeEntrada(IEndpointRouteBuilder rotas)
    {
        rotas.MapPost("/d/{slug}/codigo", async (
            string slug,
            [FromForm] string email,
            DomainService dominios,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var resultado = await dominios.RequestEntryCodeAsync(
                slug, email, contexto.Connection.RemoteIpAddress?.ToString(), cancellationToken);

            var endereco = $"/d/{Uri.EscapeDataString(slug)}";

            return resultado switch
            {
                DomainEntryFailure.DomainNotFound => Results.Redirect("/nao-encontrado"),
                DomainEntryFailure.InvalidEmail => Results.Redirect(
                    $"{endereco}?erro={Uri.EscapeDataString("Endereço de email inválido.")}"),
                DomainEntryFailure.EmailNotAccepted => Results.Redirect(
                    $"{endereco}?erro={Uri.EscapeDataString("Este endereço não pertence ao domínio liberado.")}"),
                DomainEntryFailure.RateLimited => Results.Redirect(
                    $"{endereco}?erro={Uri.EscapeDataString("Pedidos demais. Aguarde alguns minutos.")}"),
                _ => Results.Redirect($"{endereco}?email={Uri.EscapeDataString(email)}&enviado=1")
            };
        });
    }
}
