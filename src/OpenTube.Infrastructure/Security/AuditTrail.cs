// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Access;
using OpenTube.Domain.Entities;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Security;

/// <summary>Ações registradas. Códigos estáveis, para que o filtro não dependa de texto.</summary>
public static class AuditActions
{
    public const string VideoEnviado = "video.enviado";
    public const string VideoAlterado = "video.alterado";
    public const string VideoExcluido = "video.excluido";
    public const string VideoRestaurado = "video.restaurado";
    public const string VideoReprocessado = "video.reprocessado";
    public const string AcessoConcedido = "acesso.concedido";
    public const string AcessoRevogado = "acesso.revogado";
    public const string AcessoRestaurado = "acesso.restaurado";
    public const string LinkCriado = "acesso.link";
    public const string ColecaoCriada = "colecao.criada";
    public const string ColecaoAlterada = "colecao.alterada";
    public const string ColecaoExcluida = "colecao.excluida";
    public const string DominioCadastrado = "dominio.cadastrado";
    public const string DominioVerificado = "dominio.verificado";
    public const string DominioAlterado = "dominio.alterado";
    public const string MarcaDefinida = "marca.definida";
    public const string MarcaReposicionada = "marca.reposicionada";
    public const string MarcaRemovida = "marca.removida";
}

/// <summary>Tipos de entidade sobre os quais uma ação recai.</summary>
public static class AuditEntities
{
    public const string Video = "video";
    public const string Colecao = "colecao";
    public const string Concessao = "concessao";
    public const string Dominio = "dominio";
    public const string Pessoa = "pessoa";
    public const string MarcaDagua = "marca";
}

/// <summary>
/// Registro de auditoria das ações administrativas. É o que permite reconstruir quem liberou
/// o quê e quando, numa discussão que costuma acontecer meses depois.
/// </summary>
public class AuditTrail(OpenTubeDbContext db, TimeProvider clock)
{
    public async Task RecordAsync(
        Viewer actor,
        string action,
        string entityType,
        Guid? entityId,
        string summary,
        string? ipHash = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        db.AuditEntries.Add(AuditEntry.Record(
            actor.UserId, actor.Email, action, entityType, entityId, summary, clock.GetUtcNow(), ipHash));

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Registro em ordem cronológica inversa, com filtros opcionais.</summary>
    public async Task<IReadOnlyList<AuditEntry>> ListAsync(
        string? entityType = null,
        Guid? entityId = null,
        string? actorEmail = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var consulta = db.AuditEntries.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(entityType))
            consulta = consulta.Where(e => e.EntityType == entityType);

        if (entityId is { } alvo)
            consulta = consulta.Where(e => e.EntityId == alvo);

        if (!string.IsNullOrWhiteSpace(actorEmail))
        {
            var normalizado = actorEmail.Trim().ToLowerInvariant();
            consulta = consulta.Where(e => e.ActorEmail == normalizado);
        }

        return await consulta
            .OrderByDescending(e => e.At)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToListAsync(cancellationToken);
    }
}
