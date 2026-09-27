// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Access;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Security;

namespace OpenTube.Infrastructure.Access;

/// <summary>Decisão de acesso junto com a concessão que a sustentou.</summary>
/// <param name="Decision">Resultado da avaliação.</param>
/// <param name="GrantId">Concessão usada, quando o acesso veio de uma.</param>
/// <param name="CountsViews">A concessão tem teto de visualizações. A mídia só segue depois que uma reprodução foi contada.</param>
public readonly record struct AccessOutcome(AccessDecision Decision, Guid? GrantId, bool CountsViews = false)
{
    public bool Allowed => Decision.Allowed;

    public AccessReason Reason => Decision.Reason;
}

/// <summary>
/// Aplica a política de acesso com os dados do banco: carrega as concessões que podem
/// alcançar o vídeo, confere o token do link e registra o uso.
/// </summary>
public class AccessService(OpenTubeDbContext db, IOptions<SecurityOptions> options, TimeProvider clock)
{
    private readonly SecurityOptions _options = options.Value;

    /// <summary>
    /// Confere o token de um link de compartilhamento e devolve o espectador com a concessão
    /// comprovada. O token é comparado pelo resumo: o segredo nunca é guardado.
    /// </summary>
    public async Task<Viewer> ResolveLinkAsync(Viewer viewer, string? linkToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        if (string.IsNullOrWhiteSpace(linkToken))
            return viewer;

        var resumo = TokenHasher.Hash(linkToken.Trim(), _options.TokenPepper);

        var concessao = await db.AccessGrants
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.SubjectType == GrantSubjectType.Link && g.SubjectValue == resumo, cancellationToken);

        return concessao is null ? viewer : viewer.PresentingLink(concessao.Id);
    }

    /// <summary>Avalia se o espectador pode assistir ao vídeo.</summary>
    public async Task<AccessOutcome> EvaluateAsync(Viewer viewer, Video video, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(video);

        if (viewer.IsAdmin || video.Visibility is not VideoVisibility.Restricted)
            return new AccessOutcome(AccessPolicy.Evaluate(viewer, video), null);

        var colecoes = await CollectionsOfAsync(video.Id, cancellationToken);
        var concessoes = await GrantsForAsync(viewer, video.Id, colecoes, cancellationToken);
        var agora = clock.GetUtcNow();

        var decisao = AccessPolicy.Evaluate(viewer, video, concessoes, colecoes, agora);

        if (!decisao.Allowed)
            return new AccessOutcome(decisao, null);

        // Descobre qual concessão sustentou a liberação, para poder registrar o uso e
        // contar visualizações no limite configurado.
        var usada = concessoes.FirstOrDefault(g => AccessPolicy.CanWatch(viewer, video, [g], colecoes, agora));

        return new AccessOutcome(decisao, usada?.Id, usada?.MaxViews is not null);
    }

    /// <summary>Coleções às quais o vídeo pertence.</summary>
    public async Task<IReadOnlyList<Guid>> CollectionsOfAsync(Guid videoId, CancellationToken cancellationToken = default) =>
        await db.CollectionVideos
            .AsNoTracking()
            .Where(cv => cv.VideoId == videoId)
            .Select(cv => cv.CollectionId)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Concessões que podem alcançar este vídeo e este espectador. O filtro já vai ao banco
    /// para não trazer o cadastro inteiro a cada reprodução.
    /// </summary>
    public async Task<IReadOnlyList<AccessGrant>> GrantsForAsync(
        Viewer viewer,
        Guid videoId,
        IReadOnlyCollection<Guid> collectionIds,
        CancellationToken cancellationToken = default)
    {
        var email = viewer.Email;
        var dominio = viewer.EmailDomain;
        var linkGrant = viewer.LinkGrantId;
        var colecoes = collectionIds.ToArray();

        return await db.AccessGrants
            .AsNoTracking()
            .Where(g =>
                (g.SubjectType == GrantSubjectType.Public
                 || (g.SubjectType == GrantSubjectType.User && email != null && g.SubjectValue == email)
                 || (g.SubjectType == GrantSubjectType.Domain && dominio != null && g.SubjectValue == dominio)
                 || (g.SubjectType == GrantSubjectType.Link && linkGrant != null && g.Id == linkGrant))
                &&
                (g.TargetType == GrantTargetType.All
                 || (g.TargetType == GrantTargetType.Video && g.TargetId == videoId)
                 || (g.TargetType == GrantTargetType.Collection && g.TargetId != null && colecoes.Contains(g.TargetId.Value))))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Registra o uso da concessão. O primeiro uso dispara a contagem do prazo relativo, e é
    /// o que permite conceder "trinta dias a partir do primeiro acesso".
    /// </summary>
    /// <returns>
    /// <c>false</c> quando o teto de visualizações já tinha sido alcançado. O incremento é uma
    /// única instrução condicional no banco: ler, somar e gravar deixaria reproduções abertas
    /// ao mesmo tempo passarem juntas do limite.
    /// </returns>
    public async Task<bool> RegisterUseAsync(Guid grantId, CancellationToken cancellationToken = default)
    {
        var agora = clock.GetUtcNow();

        var alteradas = await db.AccessGrants
            .Where(g => g.Id == grantId && (g.MaxViews == null || g.ViewsUsed < g.MaxViews))
            .ExecuteUpdateAsync(s => s
                .SetProperty(g => g.ViewsUsed, g => g.ViewsUsed + 1)
                .SetProperty(g => g.FirstUsedAt, g => g.FirstUsedAt ?? agora),
                cancellationToken);

        // A atualização em lote não passa pelo rastreador: uma cópia já carregada neste
        // contexto ficaria com a contagem antiga.
        foreach (var rastreada in db.ChangeTracker.Entries<AccessGrant>()
                     .Where(e => e.Entity.Id == grantId)
                     .ToList())
        {
            rastreada.State = EntityState.Detached;
        }

        return alteradas == 1;
    }
}
