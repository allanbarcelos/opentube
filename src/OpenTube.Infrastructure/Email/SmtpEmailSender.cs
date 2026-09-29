// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net.Security;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using OpenTube.Infrastructure.Options;

namespace OpenTube.Infrastructure.Email;

/// <summary>Envio por SMTP. Em desenvolvimento aponta para o Mailpit, que retém as mensagens.</summary>
public class SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly SmtpOptions _options = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.FromName, _options.From));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder
        {
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody
        }.ToMessageBody();

        using var client = new SmtpClient();

        if (_options.AcceptSelfSignedCertificate)
            client.ServerCertificateValidationCallback = (_, _, _, erros) => AcceptsCertificate(erros, acceptSelfSigned: true);

        var seguranca = _options.UseStartTls
            ? SecureSocketOptions.StartTls
            : SecureSocketOptions.Auto;

        await client.ConnectAsync(_options.Host, _options.Port, seguranca, cancellationToken);

        // Sem usuário, o servidor aceita sem login; com usuário, a senha pode ser vazia.
        if (!string.IsNullOrWhiteSpace(_options.Username))
            await client.AuthenticateAsync(_options.Username, _options.Password ?? string.Empty, cancellationToken);

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);

        logger.LogInformation("Email enviado para {Destinatario}: {Assunto}", message.To, message.Subject);
    }

    /// <summary>
    /// Se o certificado do servidor SMTP é aceito. Com a opção ligada, só a falta de uma
    /// autoridade confiável é relevada; nome errado ou certificado ausente continuam recusados.
    /// </summary>
    public static bool AcceptsCertificate(SslPolicyErrors errors, bool acceptSelfSigned) =>
        errors == SslPolicyErrors.None
        || (acceptSelfSigned && errors == SslPolicyErrors.RemoteCertificateChainErrors);
}
