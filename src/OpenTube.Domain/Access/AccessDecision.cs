namespace OpenTube.Domain.Access;

/// <summary>Motivo pelo qual o acesso foi concedido ou negado.</summary>
public enum AccessReason
{
    /// <summary>Administradores enxergam todo o acervo.</summary>
    Administrator = 0,

    /// <summary>O vídeo está marcado como público.</summary>
    PublicVideo = 1,

    /// <summary>Existe uma concessão válida para quem está pedindo.</summary>
    GrantedToUser = 2,

    /// <summary>O email pertence a um domínio com concessão válida.</summary>
    GrantedToDomain = 3,

    /// <summary>O visitante apresentou um token de link válido.</summary>
    GrantedByLink = 4,

    /// <summary>O vídeo é privado e não há concessão possível.</summary>
    PrivateVideo = 10,

    /// <summary>O vídeo é restrito e não há concessão para este espectador.</summary>
    NoGrant = 11,

    /// <summary>Existe concessão, mas fora da janela de validade.</summary>
    GrantExpired = 12,

    /// <summary>Existe concessão, mas ainda não começou a valer.</summary>
    GrantNotStarted = 13,

    /// <summary>A concessão foi revogada.</summary>
    GrantRevoked = 14,

    /// <summary>A concessão atingiu o limite de visualizações.</summary>
    GrantExhausted = 15,

    /// <summary>O vídeo foi excluído.</summary>
    VideoDeleted = 20,

    /// <summary>O vídeo ainda não terminou de ser processado.</summary>
    VideoNotReady = 21,

    /// <summary>A conta do espectador está desativada.</summary>
    ViewerDisabled = 22,

    /// <summary>Reproduções simultâneas demais com a mesma conta.</summary>
    TooManyStreams = 23
}

/// <summary>Resultado da avaliação de acesso, com o motivo preservado para auditoria.</summary>
/// <param name="Allowed">Se o acesso foi concedido.</param>
/// <param name="Reason">Motivo da decisão.</param>
public readonly record struct AccessDecision(bool Allowed, AccessReason Reason)
{
    public static AccessDecision Allow(AccessReason reason) => new(true, reason);

    public static AccessDecision Deny(AccessReason reason) => new(false, reason);
}
