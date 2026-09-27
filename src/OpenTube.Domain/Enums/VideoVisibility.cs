// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Enums;

/// <summary>Quem pode assistir a um vídeo, antes de qualquer concessão individual.</summary>
public enum VideoVisibility
{
    /// <summary>Padrão de todo upload: somente administradores.</summary>
    Private = 0,

    /// <summary>Qualquer visitante do site, sem autenticação.</summary>
    Public = 1,

    /// <summary>Somente quem possui uma concessão de acesso válida.</summary>
    Restricted = 2
}
