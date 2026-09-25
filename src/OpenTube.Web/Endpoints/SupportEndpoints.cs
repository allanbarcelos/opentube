using Microsoft.AspNetCore.Mvc;
using OpenTube.Infrastructure.Support;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>
/// Conversas de suporte. O autor escreve pela página do vídeo; a administração responde pela
/// própria fila.
/// </summary>
public static class SupportEndpoints
{
    public static IEndpointRouteBuilder MapSupportEndpoints(this IEndpointRouteBuilder rotas)
    {
        var grupo = rotas.MapGroup("/suporte").RequireAuthorization();

        grupo.MapPost("/abrir", async (
            [FromForm] Guid videoId,
            [FromForm] string mensagem,
            [FromForm] string destino,
            [FromForm] double? instante,
            SupportService suporte,
            CurrentViewer espectadores,
            CancellationToken cancellationToken) =>
        {
            var espectador = await espectadores.GetAsync(cancellationToken);

            try
            {
                await suporte.OpenAsync(videoId, espectador, mensagem, instante, cancellationToken);

                return Results.Redirect(Voltar(destino, "conversa=1"));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Results.Redirect(Voltar(destino, "erro=" + Uri.EscapeDataString(e.Message)));
            }
        });

        grupo.MapPost("/{threadId:guid}/responder", async (
            Guid threadId,
            [FromForm] string mensagem,
            [FromForm] string destino,
            SupportService suporte,
            CurrentViewer espectadores,
            CancellationToken cancellationToken) =>
        {
            var espectador = await espectadores.GetAsync(cancellationToken);

            try
            {
                await suporte.ReplyAsync(threadId, espectador, mensagem, cancellationToken);

                return Results.Redirect(Voltar(destino, "respondida=1"));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Results.Redirect(Voltar(destino, "erro=" + Uri.EscapeDataString(e.Message)));
            }
        });

        var administracao = rotas.MapGroup("/admin/suporte").RequireAuthorization(Policies.Administrator);

        administracao.MapPost("/{threadId:guid}/encerrar", async (
            Guid threadId,
            SupportService suporte,
            CurrentViewer espectadores,
            CancellationToken cancellationToken) =>
        {
            await suporte.CloseAsync(threadId, await espectadores.GetAsync(cancellationToken), cancellationToken);

            return Results.Redirect($"/admin/suporte/{threadId}?encerrada=1");
        });

        administracao.MapPost("/{threadId:guid}/reabrir", async (
            Guid threadId,
            SupportService suporte,
            CurrentViewer espectadores,
            CancellationToken cancellationToken) =>
        {
            await suporte.ReopenAsync(threadId, await espectadores.GetAsync(cancellationToken), cancellationToken);

            return Results.Redirect($"/admin/suporte/{threadId}?reaberta=1");
        });

        return rotas;
    }

    /// <summary>
    /// Volta para a página de onde o formulário veio. O destino é sempre um caminho interno:
    /// aceitar um endereço completo permitiria usar o formulário para redirecionar alguém
    /// para fora do site.
    /// </summary>
    private static string Voltar(string? destino, string parametro)
    {
        var caminho = string.IsNullOrWhiteSpace(destino) || !destino.StartsWith('/') || destino.StartsWith("//")
            ? "/"
            : destino;

        return caminho + (caminho.Contains('?') ? "&" : "?") + parametro;
    }
}
