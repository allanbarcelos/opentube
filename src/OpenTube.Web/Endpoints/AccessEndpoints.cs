// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Access;
using OpenTube.Infrastructure.Localization;
using OpenTube.Infrastructure.Security;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>
/// Convites e revogações pela área administrativa. Cada convite é independente: criar um novo
/// nunca mexe nos anteriores. Os formulários voltam para a lista de convites do alvo (a aba
/// de acesso do vídeo, ou a página da coleção).
/// </summary>
public static class AccessEndpoints
{
    public static IEndpointRouteBuilder MapAccessEndpoints(this IEndpointRouteBuilder rotas)
    {
        var grupo = rotas.MapGroup("/admin/access").RequireAuthorization(Policies.Administrator);

        // Uma ou várias pessoas, com a mesma validade, num convite.
        grupo.MapPost("/invite", async (
            [FromForm] int alvoTipo,
            [FromForm] Guid? alvoId,
            [FromForm] string? emails,
            [FromForm] string? validade,
            [FromForm] string? valorDaValidade,
            [FromForm] string? dias,
            [FromForm] string? dataFinal,
            [FromForm] string? nota,
            [FromForm] string? enviarEmail,
            [FromForm] string? escolhaDoEmail,
            GrantService concessoes,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var admin = ViewerContext.From(contexto.User);
            var tipo = (GrantTargetType)alvoTipo;

            try
            {
                var prazo = MontarValidade(validade, valorDaValidade, dias, dataFinal);

                // Sem a escolha no formulário (formulários antigos), o email sai como sempre saiu.
                var enviar = escolhaDoEmail is null || enviarEmail is "1" or "on" or "true";

                var resultados = await concessoes.InviteAsync(
                    SepararEmails(emails), tipo, alvoId, prazo, admin.UserId!.Value, nota, enviar, cancellationToken);

                await contexto.RegistrarAsync(
                    AuditActions.AcessoConcedido, TipoDeEntidade(tipo), alvoId,
                    LocalText.Format("Invite sent to {0} ({1})", string.Join(", ", resultados.Select(r => r.Email)), prazo.Describe()),
                    cancellationToken);

                return Results.Redirect(Retorno.Para(Destino(tipo, alvoId), $"convidados={resultados.Count}"));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Erro(tipo, alvoId, e);
            }
        });

        // Um ou vários domínios inteiros, com a mesma validade, num convite.
        grupo.MapPost("/domain", async (
            [FromForm] int alvoTipo,
            [FromForm] Guid? alvoId,
            [FromForm] string? dominios,
            [FromForm] string? dominio,
            [FromForm] string? validade,
            [FromForm] string? valorDaValidade,
            [FromForm] string? dias,
            [FromForm] string? dataFinal,
            [FromForm] string? nota,
            GrantService concessoes,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var admin = ViewerContext.From(contexto.User);
            var tipo = (GrantTargetType)alvoTipo;

            try
            {
                var prazo = MontarValidade(validade, valorDaValidade, dias, dataFinal);
                var lista = SepararEmails(dominios ?? dominio).ToList();

                var concedidos = await concessoes.GrantToDomainsAsync(
                    lista, tipo, alvoId, prazo, admin.UserId!.Value, nota, cancellationToken);

                await contexto.RegistrarAsync(
                    AuditActions.AcessoConcedido, TipoDeEntidade(tipo), alvoId,
                    LocalText.Format("Domain {0} granted ({1})", string.Join(", ", concedidos.Select(c => c.SubjectValue)), prazo.Describe()),
                    cancellationToken);

                return Results.Redirect(Retorno.Para(Destino(tipo, alvoId), $"dominios={concedidos.Count}"));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Erro(tipo, alvoId, e);
            }
        });

        grupo.MapPost("/link", async (
            [FromForm] int alvoTipo,
            [FromForm] Guid? alvoId,
            [FromForm] string? validade,
            [FromForm] string? valorDaValidade,
            [FromForm] string? dias,
            [FromForm] string? dataFinal,
            [FromForm] string? limiteDeVisualizacoes,
            [FromForm] string? nota,
            GrantService concessoes,
            ShareLinkFlash flash,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            var admin = ViewerContext.From(contexto.User);
            var tipo = (GrantTargetType)alvoTipo;

            try
            {
                var prazo = MontarValidade(validade, valorDaValidade, dias, dataFinal);

                var link = await concessoes.CreateShareLinkAsync(
                    tipo, alvoId, prazo, admin.UserId!.Value, LimiteDeVisualizacoes(limiteDeVisualizacoes), nota, cancellationToken);

                await contexto.RegistrarAsync(
                    AuditActions.LinkCriado, TipoDeEntidade(tipo), alvoId,
                    LocalText.Format("Share link created ({0})", prazo.Describe()),
                    cancellationToken);

                // O endereço é guardado no servidor e recuperado uma única vez pela página:
                // mandá-lo na URL o deixaria no histórico e nos registros de acesso.
                return Results.Redirect(Retorno.Para(Destino(tipo, alvoId), $"link={flash.Store(link.Url)}"));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Erro(tipo, alvoId, e);
            }
        });

        // Uma pessoa, domínio ou link, sozinho.
        grupo.MapPost("/{grantId:guid}/revoke", async (
            Guid grantId, [FromForm] int alvoTipo, [FromForm] Guid? alvoId,
            GrantService concessoes, HttpContext contexto, CancellationToken cancellationToken) =>
        {
            await concessoes.RevokeAsync(grantId, cancellationToken);

            await contexto.RegistrarAsync(
                AuditActions.AcessoRevogado, AuditEntities.Concessao, grantId, LocalText.Get("Access revoked."), cancellationToken);

            return Results.Redirect(Retorno.Para(Destino((GrantTargetType)alvoTipo, alvoId), "acesso-revogado=1"));
        });

        grupo.MapPost("/{grantId:guid}/restore", async (
            Guid grantId, [FromForm] int alvoTipo, [FromForm] Guid? alvoId,
            GrantService concessoes, HttpContext contexto, CancellationToken cancellationToken) =>
        {
            await concessoes.RestoreAsync(grantId, cancellationToken);

            await contexto.RegistrarAsync(
                AuditActions.AcessoRestaurado, AuditEntities.Concessao, grantId, LocalText.Get("Access restored"), cancellationToken);

            return Results.Redirect(Retorno.Para(Destino((GrantTargetType)alvoTipo, alvoId), "acesso-restaurado=1"));
        });

        // O convite inteiro.
        grupo.MapPost("/invitations/{invitationId:guid}/revoke", async (
            Guid invitationId, [FromForm] int alvoTipo, [FromForm] Guid? alvoId,
            GrantService concessoes, HttpContext contexto, CancellationToken cancellationToken) =>
        {
            var tipo = (GrantTargetType)alvoTipo;
            try
            {
                await concessoes.RevokeInvitationAsync(invitationId, cancellationToken);

                await contexto.RegistrarAsync(
                    AuditActions.AcessoRevogado, TipoDeEntidade(tipo), alvoId, LocalText.Get("Invitation revoked"), cancellationToken);

                return Results.Redirect(Retorno.Para(Destino(tipo, alvoId), "convite-revogado=1"));
            }
            catch (InvalidOperationException e)
            {
                return Erro(tipo, alvoId, e);
            }
        });

        grupo.MapPost("/invitations/{invitationId:guid}/restore", async (
            Guid invitationId, [FromForm] int alvoTipo, [FromForm] Guid? alvoId,
            GrantService concessoes, HttpContext contexto, CancellationToken cancellationToken) =>
        {
            var tipo = (GrantTargetType)alvoTipo;
            try
            {
                await concessoes.RestoreInvitationAsync(invitationId, cancellationToken);

                await contexto.RegistrarAsync(
                    AuditActions.AcessoRestaurado, TipoDeEntidade(tipo), alvoId, LocalText.Get("Invitation restored"), cancellationToken);

                return Results.Redirect(Retorno.Para(Destino(tipo, alvoId), "convite-restaurado=1"));
            }
            catch (InvalidOperationException e)
            {
                return Erro(tipo, alvoId, e);
            }
        });

        return rotas;
    }

    private static IResult Erro(GrantTargetType tipo, Guid? alvoId, Exception e) =>
        Results.Redirect(Retorno.Para(Destino(tipo, alvoId), "erro-acesso=" + Uri.EscapeDataString(LocalText.Get(e.Message))));

    /// <summary>Tipo de entidade da auditoria, conforme o alvo da concessão.</summary>
    private static string TipoDeEntidade(GrantTargetType tipo) => tipo switch
    {
        GrantTargetType.Collection => AuditEntities.Colecao,
        GrantTargetType.Video => AuditEntities.Video,
        _ => AuditEntities.Concessao
    };

    /// <summary>Onde fica a lista de convites do alvo.</summary>
    private static string Destino(GrantTargetType tipo, Guid? alvoId) => tipo switch
    {
        GrantTargetType.Video => $"/admin/videos/{alvoId}?tab=access",
        GrantTargetType.Collection => $"/admin/collections/{alvoId}",
        _ => "/admin"
    };

    /// <summary>
    /// Converte a escolha do formulário na validade. Um número de dias ou uma data que não
    /// servem são recusados com o motivo: virar "sem prazo" em silêncio daria acesso eterno a
    /// quem devia ter prazo.
    /// </summary>
    /// <param name="modo">"sempre", "dias" ou "ate".</param>
    /// <param name="valor">Campo antigo, único para dias e data.</param>
    /// <param name="dias">Número de dias a partir do primeiro acesso.</param>
    /// <param name="data">Último dia de acesso (aaaa-mm-dd), incluído inteiro.</param>
    public static GrantValidity MontarValidade(string? modo, string? valor, string? dias = null, string? data = null)
    {
        switch (modo)
        {
            case "dias":
                var textoDias = string.IsNullOrWhiteSpace(dias) ? valor : dias;
                if (!int.TryParse(textoDias, NumberStyles.None, CultureInfo.InvariantCulture, out var quantos) || quantos is < 1 or > 3650)
                    throw new InvalidOperationException("Enter the number of days, from 1 to 3650.");
                return GrantValidity.For(TimeSpan.FromDays(quantos));

            case "ate":
                var textoData = string.IsNullOrWhiteSpace(data) ? valor : data;

                // Só a data: vale até o fim desse dia.
                if (DateOnly.TryParseExact(textoData, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dia))
                {
                    var meiaNoite = dia.AddDays(1).ToDateTime(TimeOnly.MinValue);
                    var fim = new DateTimeOffset(meiaNoite, TimeZoneInfo.Local.GetUtcOffset(meiaNoite));
                    // O banco guarda instantes em UTC; o fim do dia é o do fuso do servidor.
                    return fim > DateTimeOffset.Now
                        ? GrantValidity.Until(fim.ToUniversalTime())
                        : throw new InvalidOperationException("Choose an end date in the future.");
                }

                if (DateTimeOffset.TryParse(textoData, CultureInfo.InvariantCulture, DateTimeStyles.None, out var quando))
                    return GrantValidity.Until(quando.ToUniversalTime());

                throw new InvalidOperationException("Choose the end date.");

            default:
                return GrantValidity.Forever;
        }
    }

    /// <summary>
    /// Lê o limite de visualizações. O campo chega vazio quando não há limite, e um vazio não
    /// se converte sozinho em número.
    /// </summary>
    public static int? LimiteDeVisualizacoes(string? valor) =>
        int.TryParse(valor, out var limite) && limite > 0 ? limite : null;

    /// <summary>Divide a lista de endereços (ou domínios) digitada pelo administrador.</summary>
    public static IEnumerable<string> SepararEmails(string? texto) =>
        string.IsNullOrWhiteSpace(texto)
            ? []
            : texto.Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
