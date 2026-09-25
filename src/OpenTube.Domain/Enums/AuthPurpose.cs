namespace OpenTube.Domain.Enums;

/// <summary>Motivo pelo qual um código de acesso foi emitido.</summary>
public enum AuthPurpose
{
    /// <summary>Entrada comum, pedida pela própria pessoa na tela de acesso.</summary>
    Login = 0,

    /// <summary>Convite enviado pelo administrador junto com uma concessão de acesso.</summary>
    Invite = 1,

    /// <summary>Entrada pela porta dedicada de um domínio verificado.</summary>
    DomainEntry = 2
}
