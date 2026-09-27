// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;

namespace OpenTube.Domain.Access;

/// <summary>
/// Decide quem pode assistir a quê. Toda a aplicação — home, busca, player, manifesto, legenda,
/// miniatura e download — passa por aqui; é o ponto em que um erro vaza conteúdo confidencial,
/// e por isso concentra a maior cobertura de testes do projeto.
/// </summary>
public static class AccessPolicy
{
    /// <summary>Avalia o acesso a um vídeo sem considerar concessões.</summary>
    public static AccessDecision Evaluate(Viewer viewer, Video video) =>
        Evaluate(viewer, video, [], [], DateTimeOffset.UtcNow);

    /// <summary>
    /// Avalia o acesso considerando as concessões existentes para o vídeo.
    /// </summary>
    /// <param name="viewer">Quem está pedindo.</param>
    /// <param name="video">Vídeo pedido.</param>
    /// <param name="grants">Concessões que podem alcançar este vídeo.</param>
    /// <param name="collectionIds">Coleções às quais o vídeo pertence.</param>
    /// <param name="now">Instante da avaliação.</param>
    public static AccessDecision Evaluate(
        Viewer viewer,
        Video video,
        IReadOnlyCollection<AccessGrant> grants,
        IReadOnlyCollection<Guid> collectionIds,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(collectionIds);

        // O administrador precisa enxergar rascunhos e vídeos em processamento para
        // acompanhar o pipeline, então a checagem dele vem antes do estado do vídeo.
        if (viewer.IsAdmin)
            return AccessDecision.Allow(AccessReason.Administrator);

        if (video.IsDeleted)
            return AccessDecision.Deny(AccessReason.VideoDeleted);

        if (video.Status is not VideoStatus.Ready)
            return AccessDecision.Deny(AccessReason.VideoNotReady);

        if (video.Visibility is VideoVisibility.Public)
            return AccessDecision.Allow(AccessReason.PublicVideo);

        if (video.Visibility is VideoVisibility.Private)
            return AccessDecision.Deny(AccessReason.PrivateVideo);

        return AvaliarConcessoes(viewer, video, grants, collectionIds, now);
    }

    /// <summary>Atalho para quando só interessa o sim ou não.</summary>
    public static bool CanWatch(Viewer viewer, Video video) => Evaluate(viewer, video).Allowed;

    /// <summary>Atalho para quando só interessa o sim ou não, com concessões.</summary>
    public static bool CanWatch(
        Viewer viewer,
        Video video,
        IReadOnlyCollection<AccessGrant> grants,
        IReadOnlyCollection<Guid> collectionIds,
        DateTimeOffset now) =>
        Evaluate(viewer, video, grants, collectionIds, now).Allowed;

    /// <summary>
    /// Procura uma concessão válida. Quando nenhuma vale, devolve o motivo da que chegou mais
    /// perto — dizer "seu acesso expirou" é muito mais útil que um "não encontrado" genérico
    /// para quem tinha acesso legítimo até ontem.
    /// </summary>
    private static AccessDecision AvaliarConcessoes(
        Viewer viewer,
        Video video,
        IReadOnlyCollection<AccessGrant> grants,
        IReadOnlyCollection<Guid> collectionIds,
        DateTimeOffset now)
    {
        var motivoDaRecusa = AccessReason.NoGrant;

        foreach (var concessao in grants)
        {
            if (!Aplicavel(concessao, viewer))
                continue;

            if (!concessao.Covers(video.Id, collectionIds))
                continue;

            if (concessao.IsActiveAt(now, ignoreViewLimit: viewer.IsContinuing(video.Id, concessao.Id)))
                return AccessDecision.Allow(MotivoDaLiberacao(concessao.SubjectType));

            motivoDaRecusa = PiorMotivo(motivoDaRecusa, MotivoDaRecusa(concessao, now));
        }

        return AccessDecision.Deny(motivoDaRecusa);
    }

    private static bool Aplicavel(AccessGrant concessao, Viewer viewer) => concessao.SubjectType switch
    {
        GrantSubjectType.Public => true,
        GrantSubjectType.User => concessao.MatchesSubject(GrantSubjectType.User, viewer.Email),
        GrantSubjectType.Domain => concessao.MatchesSubject(GrantSubjectType.Domain, viewer.EmailDomain),
        // O token do link já foi conferido fora daqui: o espectador chega com a concessão
        // que ele comprovou possuir, e não com o segredo em si.
        GrantSubjectType.Link => viewer.LinkGrantId == concessao.Id,
        _ => false
    };

    private static AccessReason MotivoDaLiberacao(GrantSubjectType tipo) => tipo switch
    {
        GrantSubjectType.Domain => AccessReason.GrantedToDomain,
        GrantSubjectType.Link => AccessReason.GrantedByLink,
        GrantSubjectType.Public => AccessReason.PublicVideo,
        _ => AccessReason.GrantedToUser
    };

    private static AccessReason MotivoDaRecusa(AccessGrant concessao, DateTimeOffset now)
    {
        if (concessao.IsRevoked)
            return AccessReason.GrantRevoked;

        if (concessao.IsExhausted)
            return AccessReason.GrantExhausted;

        if (concessao.StartsAt is { } inicio && now < inicio)
            return AccessReason.GrantNotStarted;

        return AccessReason.GrantExpired;
    }

    /// <summary>
    /// Entre vários motivos de recusa, o mais informativo vence. "Não há concessão" é o menos
    /// útil de todos e só aparece quando nada mais se aplica.
    /// </summary>
    private static AccessReason PiorMotivo(AccessReason atual, AccessReason novo) =>
        atual is AccessReason.NoGrant ? novo : atual;
}
