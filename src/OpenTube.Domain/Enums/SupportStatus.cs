// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Enums;

/// <summary>Situação de uma conversa de suporte.</summary>
public enum SupportStatus
{
    /// <summary>Aguardando resposta do administrador.</summary>
    Open = 0,

    /// <summary>O administrador respondeu; a bola está com quem perguntou.</summary>
    Answered = 1,

    /// <summary>Encerrada.</summary>
    Closed = 2
}
