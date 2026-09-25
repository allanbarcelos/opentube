using Microsoft.AspNetCore.Mvc;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Access;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>
/// Concessão e revogação de acesso pela área administrativa. Cada formulário volta para a
/// página do alvo, que é onde o administrador acompanha quem tem acesso a quê.
/// </summary>
public static class AccessEndpoints
{
    public static IEndpointRouteBuilder MapAccessEndpoints(this IEndpointRouteBuilder rotas)
    {
        var grupo = rotas.MapGroup("/admin/acessos").RequireAuthorization(Policies.Administrator);

        grupo.MapPost("/convidar", async (
            [FromForm] int alvoTipo,
            [FromForm] Guid? alvoId,
            [FromForm] string emails,
            [FromForm] string validade,
            [FromForm] string? valorDaValidade,
            [FromForm] string? nota,
            GrantService concessoes,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var admin = ViewerContext.From(contexto.User);
            var destino = Destino((GrantTargetType)alvoTipo, alvoId);

            try
            {
                var resultados = await concessoes.InviteAsync(
                    SepararEmails(emails),
                    (GrantTargetType)alvoTipo,
                    alvoId,
                    MontarValidade(validade, valorDaValidade),
                    admin.UserId!.Value,
                    nota,
                    cancellationToken: cancellationToken);

                return Results.Redirect($"{destino}?convidados={resultados.Count}");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Results.Redirect($"{destino}?erro={Uri.EscapeDataString(e.Message)}");
            }
        });

        grupo.MapPost("/dominio", async (
            [FromForm] int alvoTipo,
            [FromForm] Guid? alvoId,
            [FromForm] string dominio,
            [FromForm] string validade,
            [FromForm] string? valorDaValidade,
            [FromForm] string? nota,
            GrantService concessoes,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var admin = ViewerContext.From(contexto.User);
            var destino = Destino((GrantTargetType)alvoTipo, alvoId);

            try
            {
                await concessoes.GrantToDomainAsync(
                    dominio,
                    (GrantTargetType)alvoTipo,
                    alvoId,
                    MontarValidade(validade, valorDaValidade),
                    admin.UserId!.Value,
                    nota,
                    cancellationToken);

                return Results.Redirect($"{destino}?dominio=1");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Results.Redirect($"{destino}?erro={Uri.EscapeDataString(e.Message)}");
            }
        });

        grupo.MapPost("/link", async (
            [FromForm] int alvoTipo,
            [FromForm] Guid? alvoId,
            [FromForm] string validade,
            [FromForm] string? valorDaValidade,
            [FromForm] string? limiteDeVisualizacoes,
            [FromForm] string? nota,
            GrantService concessoes,
            ShareLinkFlash flash,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var admin = ViewerContext.From(contexto.User);
            var destino = Destino((GrantTargetType)alvoTipo, alvoId);

            try
            {
                var link = await concessoes.CreateShareLinkAsync(
                    (GrantTargetType)alvoTipo,
                    alvoId,
                    MontarValidade(validade, valorDaValidade),
                    admin.UserId!.Value,
                    LimiteDeVisualizacoes(limiteDeVisualizacoes),
                    nota,
                    cancellationToken);

                // O endereço é guardado no servidor e recuperado uma única vez pela página:
                // mandá-lo na URL o deixaria no histórico e nos registros de acesso.
                return Results.Redirect($"{destino}?link={flash.Store(link.Url)}");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Results.Redirect($"{destino}?erro={Uri.EscapeDataString(e.Message)}");
            }
        });

        grupo.MapPost("/{grantId:guid}/revogar", async (
            Guid grantId,
            [FromForm] int alvoTipo,
            [FromForm] Guid? alvoId,
            GrantService concessoes,
            CancellationToken cancellationToken) =>
        {
            await concessoes.RevokeAsync(grantId, cancellationToken);

            return Results.Redirect($"{Destino((GrantTargetType)alvoTipo, alvoId)}?revogado=1");
        });

        grupo.MapPost("/{grantId:guid}/restaurar", async (
            Guid grantId,
            [FromForm] int alvoTipo,
            [FromForm] Guid? alvoId,
            GrantService concessoes,
            CancellationToken cancellationToken) =>
        {
            await concessoes.RestoreAsync(grantId, cancellationToken);

            return Results.Redirect($"{Destino((GrantTargetType)alvoTipo, alvoId)}?restaurado=1");
        });

        return rotas;
    }

    /// <summary>Página para onde o formulário volta, conforme o alvo da concessão.</summary>
    private static string Destino(GrantTargetType tipo, Guid? alvoId) => tipo switch
    {
        GrantTargetType.Video => $"/admin/videos/{alvoId}",
        GrantTargetType.Collection => $"/admin/colecoes/{alvoId}",
        _ => "/admin"
    };

    /// <summary>Converte a escolha do formulário na validade da concessão.</summary>
    public static GrantValidity MontarValidade(string? modo, string? valor) => modo switch
    {
        "ate" when DateTimeOffset.TryParse(valor, out var quando) => GrantValidity.Until(quando),
        "dias" when int.TryParse(valor, out var dias) && dias > 0 => GrantValidity.For(TimeSpan.FromDays(dias)),
        _ => GrantValidity.Forever
    };

    /// <summary>
    /// Lê o limite de visualizações. O campo chega vazio quando não há limite, e um vazio não
    /// se converte sozinho em número.
    /// </summary>
    public static int? LimiteDeVisualizacoes(string? valor) =>
        int.TryParse(valor, out var limite) && limite > 0 ? limite : null;

    /// <summary>Divide a lista de endereços digitada pelo administrador.</summary>
    public static IEnumerable<string> SepararEmails(string? texto) =>
        string.IsNullOrWhiteSpace(texto)
            ? []
            : texto.Split([',', ';', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
