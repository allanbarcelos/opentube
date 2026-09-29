// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Localization;

namespace OpenTube.Infrastructure.Email;

/// <summary>
/// Monta as mensagens do sistema. O HTML é propositalmente simples e com estilo embutido:
/// cliente de email corporativo ignora folha de estilo externa e costuma cortar o que não
/// reconhece.
/// </summary>
public static class EmailTemplates
{
    /// <summary>
    /// Convite enviado pelo administrador junto com uma concessão. Diz o que foi liberado e
    /// até quando, porque quem recebe precisa saber o que ganhou sem ter de entrar para
    /// descobrir.
    /// </summary>
    public static EmailMessage Invite(
        string to,
        string code,
        string link,
        string whatWasShared,
        string validityDescription,
        TimeSpan linkValidity)
    {
        var dias = Math.Max(1, (int)Math.Round(linkValidity.TotalDays));
        var prazo = LocalText.Format("The link and the code are valid for {0} days and can be used only once. After that, ask for a new code on the sign-in page with this same email.", dias);

        var texto = $"""
            {LocalText.Format("You were given access to {0} on OpenTube.", whatWasShared)}

            {LocalText.Format("Access ends: {0}", validityDescription)}

            {LocalText.Get("Sign in with this link:")}
            {link}

            {LocalText.Format("Or use the code: {0}", code)}

            {prazo}
            """;

        var html = $"""
            <div style="font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;max-width:480px;margin:0 auto;padding:24px;color:#212529">
              <h1 style="font-size:20px;margin:0 0 16px">OpenTube</h1>
              <p style="margin:0 0 8px">{WebUtility.HtmlEncode(LocalText.Format("You were given access to {0}.", whatWasShared))}</p>
              <p style="margin:0 0 24px;color:#6c757d">{WebUtility.HtmlEncode(LocalText.Format("Access ends: {0}", validityDescription))}</p>
              <p style="margin:0 0 24px">
                <a href="{WebUtility.HtmlEncode(link)}" style="background:#0d6efd;color:#fff;text-decoration:none;padding:12px 20px;border-radius:6px;display:inline-block">{WebUtility.HtmlEncode(LocalText.Get("See the videos"))}</a>
              </p>
              <p style="margin:0 0 8px">{WebUtility.HtmlEncode(LocalText.Get("Or use the code:"))}</p>
              <p style="font-size:32px;font-weight:700;letter-spacing:6px;margin:0 0 24px">{WebUtility.HtmlEncode(code)}</p>
              <p style="font-size:14px;color:#6c757d;margin:0">{WebUtility.HtmlEncode(prazo)}</p>
            </div>
            """;

        return new EmailMessage(to, LocalText.Format("You were given access to {0}", whatWasShared), html, texto);
    }

    /// <summary>Aviso à administração de que alguém escreveu sobre um vídeo.</summary>
    public static EmailMessage SupportForAdmin(string to, string author, string videoTitle, string message, string link)
    {
        var texto = $"""
            {LocalText.Format("{0} wrote about \"{1}\":", author, videoTitle)}

            {Resumir(message)}

            {LocalText.Get("Reply here:")}
            {link}
            """;

        var html = $"""
            <div style="font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;max-width:480px;margin:0 auto;padding:24px;color:#212529">
              <h1 style="font-size:20px;margin:0 0 16px">OpenTube</h1>
              <p style="margin:0 0 8px">{WebUtility.HtmlEncode(LocalText.Format("{0} wrote about \"{1}\":", author, videoTitle))}</p>
              <blockquote style="margin:0 0 24px;padding:12px 16px;border-left:3px solid #dee2e6;color:#495057;white-space:pre-wrap">{WebUtility.HtmlEncode(Resumir(message))}</blockquote>
              <p style="margin:0">
                <a href="{WebUtility.HtmlEncode(link)}" style="background:#0d6efd;color:#fff;text-decoration:none;padding:12px 20px;border-radius:6px;display:inline-block">{WebUtility.HtmlEncode(LocalText.Get("Reply"))}</a>
              </p>
            </div>
            """;

        return new EmailMessage(to, LocalText.Format("New message about {0}", videoTitle), html, texto);
    }

    /// <summary>Aviso a quem perguntou de que a administração respondeu.</summary>
    public static EmailMessage SupportForUser(string to, string videoTitle, string message, string link)
    {
        var texto = $"""
            {LocalText.Format("You received a reply about \"{0}\":", videoTitle)}

            {Resumir(message)}

            {LocalText.Get("Continue the conversation on the video page:")}
            {link}
            """;

        var html = $"""
            <div style="font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;max-width:480px;margin:0 auto;padding:24px;color:#212529">
              <h1 style="font-size:20px;margin:0 0 16px">OpenTube</h1>
              <p style="margin:0 0 8px">{WebUtility.HtmlEncode(LocalText.Format("You received a reply about \"{0}\":", videoTitle))}</p>
              <blockquote style="margin:0 0 24px;padding:12px 16px;border-left:3px solid #dee2e6;color:#495057;white-space:pre-wrap">{WebUtility.HtmlEncode(Resumir(message))}</blockquote>
              <p style="margin:0">
                <a href="{WebUtility.HtmlEncode(link)}" style="background:#0d6efd;color:#fff;text-decoration:none;padding:12px 20px;border-radius:6px;display:inline-block">{WebUtility.HtmlEncode(LocalText.Get("See the conversation"))}</a>
              </p>
            </div>
            """;

        return new EmailMessage(to, LocalText.Format("Reply about {0}", videoTitle), html, texto);
    }

    /// <summary>
    /// Trecho da mensagem para o aviso. O email leva só o começo: a conversa inteira fica na
    /// plataforma, onde o acesso é conferido.
    /// </summary>
    private static string Resumir(string mensagem)
    {
        var texto = (mensagem ?? string.Empty).Trim();

        return texto.Length <= 300 ? texto : texto[..300] + "…";
    }

    public static EmailMessage AccessCode(string to, string code, string link, AuthPurpose purpose, TimeSpan validity)
    {
        var assunto = purpose switch
        {
            AuthPurpose.Invite => LocalText.Get("You now have access to videos on OpenTube"),
            _ => LocalText.Get("Your OpenTube access code")
        };

        var abertura = purpose switch
        {
            AuthPurpose.Invite => LocalText.Get("Videos were shared with you."),
            _ => LocalText.Get("We received an access request for this email address.")
        };

        var minutos = (int)Math.Round(validity.TotalMinutes);
        var aviso = LocalText.Get("If you did not ask for this access, ignore this message.");
        var validade = LocalText.Format("The code is valid for {0} minutes and can be used only once.", minutos);

        var texto = $"""
            {abertura}

            {LocalText.Format("Your access code is: {0}", code)}

            {LocalText.Get("Or sign in directly with this link:")}
            {link}

            {validade}
            {aviso}
            """;

        var html = $"""
            <div style="font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;max-width:480px;margin:0 auto;padding:24px;color:#212529">
              <h1 style="font-size:20px;margin:0 0 16px">OpenTube</h1>
              <p style="margin:0 0 16px">{WebUtility.HtmlEncode(abertura)}</p>
              <p style="margin:0 0 8px">{WebUtility.HtmlEncode(LocalText.Get("Your access code:"))}</p>
              <p style="font-size:32px;font-weight:700;letter-spacing:6px;margin:0 0 24px">{WebUtility.HtmlEncode(code)}</p>
              <p style="margin:0 0 24px">
                <a href="{WebUtility.HtmlEncode(link)}" style="background:#0d6efd;color:#fff;text-decoration:none;padding:12px 20px;border-radius:6px;display:inline-block">{WebUtility.HtmlEncode(LocalText.Get("Sign in now"))}</a>
              </p>
              <p style="font-size:14px;color:#6c757d;margin:0 0 8px">{WebUtility.HtmlEncode(validade)}</p>
              <p style="font-size:14px;color:#6c757d;margin:0">{WebUtility.HtmlEncode(aviso)}</p>
            </div>
            """;

        return new EmailMessage(to, assunto, html, texto);
    }
}
