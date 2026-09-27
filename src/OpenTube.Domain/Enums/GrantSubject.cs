// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Enums;

/// <summary>A quem uma concessão de acesso se aplica.</summary>
public enum GrantSubjectType
{
    /// <summary>Qualquer visitante, sem identificação.</summary>
    Public = 0,

    /// <summary>Um endereço de email específico.</summary>
    User = 1,

    /// <summary>Qualquer email de um domínio verificado.</summary>
    Domain = 2,

    /// <summary>Quem apresentar um token secreto de compartilhamento.</summary>
    Link = 3
}

/// <summary>Sobre o que uma concessão de acesso recai.</summary>
public enum GrantTargetType
{
    /// <summary>Um vídeo específico.</summary>
    Video = 0,

    /// <summary>Uma coleção inteira, inclusive os vídeos acrescentados depois.</summary>
    Collection = 1,

    /// <summary>Todo o acervo.</summary>
    All = 2
}
