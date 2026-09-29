// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Enums;

/// <summary>Motivo pelo qual um código de acesso foi emitido.</summary>
public enum AuthPurpose
{
    /// <summary>Entrada comum, pedida pela própria pessoa na tela de acesso.</summary>
    Login = 0,

    /// <summary>Convite enviado pelo administrador junto com uma concessão de acesso.</summary>
    Invite = 1,

    /// <summary>
    /// Entrada pela antiga porta dedicada de um domínio, que não existe mais. O valor fica
    /// porque está gravado nos códigos emitidos antes.
    /// </summary>
    DomainEntry = 2
}
