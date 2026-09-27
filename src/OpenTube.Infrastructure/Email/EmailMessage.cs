// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Infrastructure.Email;

/// <summary>Uma mensagem pronta para envio.</summary>
/// <param name="To">Destinatário.</param>
/// <param name="Subject">Assunto.</param>
/// <param name="HtmlBody">Corpo em HTML.</param>
/// <param name="TextBody">Corpo em texto puro, para clientes que não exibem HTML.</param>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody);

/// <summary>Envio de email transacional.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
