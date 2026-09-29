// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Infrastructure.Options;

/// <summary>Configuração do envio de email transacional.</summary>
public class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 1025;

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>Remetente dos emails.</summary>
    public string From { get; set; } = "nao-responda@opentube.local";

    public string FromName { get; set; } = "OpenTube";

    /// <summary>Usa STARTTLS. Desligado no ambiente local, obrigatório em produção.</summary>
    public bool UseStartTls { get; set; }

    /// <summary>
    /// Aceita o certificado do servidor SMTP mesmo sem uma autoridade confiável por trás
    /// (autoassinado ou de uma CA interna). O nome no certificado continua tendo de bater com
    /// o servidor. Desligado por padrão: ligar abre espaço para alguém no meio do caminho ler
    /// os códigos de acesso.
    /// </summary>
    public bool AcceptSelfSignedCertificate { get; set; }
}
