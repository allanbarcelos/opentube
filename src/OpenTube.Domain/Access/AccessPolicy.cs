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
    /// <summary>Avalia se o espectador pode assistir ao vídeo.</summary>
    public static AccessDecision Evaluate(Viewer viewer, Video video)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(video);

        // O administrador precisa enxergar rascunhos e vídeos em processamento para
        // acompanhar o pipeline, então a checagem dele vem antes do estado do vídeo.
        if (viewer.IsAdmin)
            return AccessDecision.Allow(AccessReason.Administrator);

        if (video.IsDeleted)
            return AccessDecision.Deny(AccessReason.VideoDeleted);

        if (video.Status is not VideoStatus.Ready)
            return AccessDecision.Deny(AccessReason.VideoNotReady);

        return video.Visibility switch
        {
            VideoVisibility.Public => AccessDecision.Allow(AccessReason.PublicVideo),
            VideoVisibility.Restricted => AccessDecision.Deny(AccessReason.NoGrant),
            _ => AccessDecision.Deny(AccessReason.PrivateVideo)
        };
    }

    /// <summary>Atalho para quando só interessa o sim ou não.</summary>
    public static bool CanWatch(Viewer viewer, Video video) => Evaluate(viewer, video).Allowed;
}
