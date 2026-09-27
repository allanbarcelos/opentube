// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Enums;

/// <summary>O que um convite libera: pessoas por email, domínios inteiros ou um link secreto.</summary>
public enum InvitationKind
{
    People = 0,
    Domains = 1,
    Link = 2
}
