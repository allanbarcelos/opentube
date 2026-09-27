// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Enums;

/// <summary>Tipo de arquivo derivado associado a um vídeo.</summary>
public enum VideoAssetKind
{
    Caption = 0,
    Chapter = 1,
    Attachment = 2,
    Sprite = 3,
    Thumbnail = 4
}
