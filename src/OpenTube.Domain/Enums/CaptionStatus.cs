// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Enums;

/// <summary>Situação de uma legenda.</summary>
public enum CaptionStatus
{
    /// <summary>Tem conteúdo e nada em andamento.</summary>
    Ready = 0,

    /// <summary>Transcrição automática em andamento no worker.</summary>
    Processing = 1,

    /// <summary>A última transcrição falhou; o conteúdo anterior, se havia, continua.</summary>
    Failed = 2
}
