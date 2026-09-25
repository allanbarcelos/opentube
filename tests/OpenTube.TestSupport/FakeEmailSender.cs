using OpenTube.Infrastructure.Email;

namespace OpenTube.TestSupport;

/// <summary>Guarda as mensagens em memória para que os testes possam inspecioná-las.</summary>
public class FakeEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _enviados = [];

    public IReadOnlyList<EmailMessage> Sent => _enviados;

    public EmailMessage? Last => _enviados.Count == 0 ? null : _enviados[^1];

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        _enviados.Add(message);
        return Task.CompletedTask;
    }

    /// <summary>Extrai o código de seis dígitos da última mensagem.</summary>
    public string LastCode()
    {
        var corpo = Last?.TextBody ?? throw new InvalidOperationException("Nenhum email foi enviado.");
        var match = System.Text.RegularExpressions.Regex.Match(corpo, @"\b(\d{6})\b");

        return match.Success ? match.Groups[1].Value : throw new InvalidOperationException("Código não encontrado no email.");
    }

    /// <summary>Extrai o token do link da última mensagem.</summary>
    public string LastToken()
    {
        var corpo = Last?.TextBody ?? throw new InvalidOperationException("Nenhum email foi enviado.");
        var match = System.Text.RegularExpressions.Regex.Match(corpo, @"/entrar/([A-Za-z0-9_-]+)");

        return match.Success ? match.Groups[1].Value : throw new InvalidOperationException("Link não encontrado no email.");
    }

    public void Clear() => _enviados.Clear();
}
