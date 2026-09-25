using System.Net;
using OpenTube.Domain.Enums;

namespace OpenTube.Infrastructure.Email;

/// <summary>
/// Monta as mensagens do sistema. O HTML é propositalmente simples e com estilo embutido:
/// cliente de email corporativo ignora folha de estilo externa e costuma cortar o que não
/// reconhece.
/// </summary>
public static class EmailTemplates
{
    public static EmailMessage AccessCode(string to, string code, string link, AuthPurpose purpose, TimeSpan validity)
    {
        var assunto = purpose switch
        {
            AuthPurpose.Invite => "Você recebeu acesso a vídeos no OpenTube",
            AuthPurpose.DomainEntry => "Seu código de acesso ao OpenTube",
            _ => "Seu código de acesso ao OpenTube"
        };

        var abertura = purpose switch
        {
            AuthPurpose.Invite => "Foram liberados vídeos para você.",
            _ => "Recebemos um pedido de acesso com este endereço de email."
        };

        var minutos = (int)Math.Round(validity.TotalMinutes);

        var texto = $"""
            {abertura}

            Seu código de acesso é: {code}

            Ou entre direto por este link:
            {link}

            O código vale por {minutos} minutos e só pode ser usado uma vez.
            Se você não pediu este acesso, ignore esta mensagem.
            """;

        var html = $"""
            <div style="font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;max-width:480px;margin:0 auto;padding:24px;color:#212529">
              <h1 style="font-size:20px;margin:0 0 16px">OpenTube</h1>
              <p style="margin:0 0 16px">{WebUtility.HtmlEncode(abertura)}</p>
              <p style="margin:0 0 8px">Seu código de acesso:</p>
              <p style="font-size:32px;font-weight:700;letter-spacing:6px;margin:0 0 24px">{WebUtility.HtmlEncode(code)}</p>
              <p style="margin:0 0 24px">
                <a href="{WebUtility.HtmlEncode(link)}" style="background:#0d6efd;color:#fff;text-decoration:none;padding:12px 20px;border-radius:6px;display:inline-block">Entrar agora</a>
              </p>
              <p style="font-size:14px;color:#6c757d;margin:0 0 8px">O código vale por {minutos} minutos e só pode ser usado uma vez.</p>
              <p style="font-size:14px;color:#6c757d;margin:0">Se você não pediu este acesso, ignore esta mensagem.</p>
            </div>
            """;

        return new EmailMessage(to, assunto, html, texto);
    }
}
