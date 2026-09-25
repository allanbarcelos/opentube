using System.Globalization;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Access;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Security;

namespace OpenTube.Infrastructure.Playback;

/// <summary>Bilhete recém-emitido, pronto para virar cookie.</summary>
/// <param name="CookieName">Nome do cookie, um por vídeo.</param>
/// <param name="Value">Conteúdo assinado.</param>
/// <param name="ExpiresAt">Até quando o bilhete vale.</param>
public readonly record struct IssuedTicket(string CookieName, string Value, DateTimeOffset ExpiresAt);

/// <summary>
/// Comprova que uma reprodução já começou e teve a visualização contada. Sem isso, a
/// reprodução que consome a última visualização de uma concessão seria barrada logo na
/// playlist da versão seguinte. O bilhete é assinado com o segredo do servidor e amarrado ao
/// vídeo e à concessão; não substitui a concessão, que continua sendo conferida a cada pedido.
/// </summary>
public class PlaybackTickets(IOptions<SecurityOptions> options, TimeProvider clock)
{
    /// <summary>Prefixo dos cookies; o restante do nome é o vídeo.</summary>
    public const string CookiePrefix = "opentube.rep.";

    /// <summary>Folga além da duração, para pausas e buffering.</summary>
    private static readonly TimeSpan Folga = TimeSpan.FromHours(2);

    /// <summary>Teto de validade, qualquer que seja a duração do vídeo.</summary>
    private static readonly TimeSpan Teto = TimeSpan.FromHours(12);

    private readonly SecurityOptions _options = options.Value;

    public IssuedTicket Issue(Guid videoId, Guid grantId, double durationSeconds)
    {
        var duracao = TimeSpan.FromSeconds(Math.Max(0, durationSeconds)) + Folga;
        var validade = clock.GetUtcNow() + (duracao < Teto ? duracao : Teto);
        var expira = validade.ToUnixTimeSeconds();

        var valor = string.Join('.',
            grantId.ToString("n"),
            expira.ToString(CultureInfo.InvariantCulture),
            Assinar(videoId, grantId, expira));

        return new IssuedTicket(CookiePrefix + videoId.ToString("n"), valor, validade);
    }

    /// <summary>
    /// Confere o bilhete de um cookie e devolve a reprodução comprovada. Qualquer coisa fora
    /// do formato, com assinatura errada ou vencida é simplesmente ignorada.
    /// </summary>
    public ViewInProgress? Validate(string cookieName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !cookieName.StartsWith(CookiePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(cookieName[CookiePrefix.Length..], "n", out var videoId))
            return null;

        var partes = value.Split('.');

        if (partes.Length != 3
            || !Guid.TryParseExact(partes[0], "n", out var grantId)
            || !long.TryParse(partes[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expira))
            return null;

        // Um prazo fora do intervalo do relógio é lixo, como uma assinatura errada: não pode
        // derrubar o pedido inteiro só porque o cookie veio malformado.
        DateTimeOffset instante;
        try
        {
            instante = DateTimeOffset.FromUnixTimeSeconds(expira);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        if (instante <= clock.GetUtcNow())
            return null;

        return TokenHasher.Verify(Conteudo(videoId, grantId, expira), Base64Padrao(partes[2]), _options.TokenPepper)
            ? new ViewInProgress(videoId, grantId)
            : null;
    }

    /// <summary>Acrescenta ao espectador as reproduções comprovadas pelos cookies.</summary>
    public Viewer Apply(Viewer viewer, IEnumerable<KeyValuePair<string, string>> cookies)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        foreach (var (nome, valor) in cookies)
        {
            if (Validate(nome, valor) is { } reproducao)
                viewer = viewer.ContinuingView(reproducao.VideoId, reproducao.GrantId);
        }

        return viewer;
    }

    private string Assinar(Guid videoId, Guid grantId, long expira) =>
        TokenHasher.Hash(Conteudo(videoId, grantId, expira), _options.TokenPepper)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Conteudo(Guid videoId, Guid grantId, long expira) =>
        $"reproducao|{videoId:n}|{grantId:n}|{expira.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Volta a assinatura do formato de URL para o base64 comum guardado pelo resumo.</summary>
    private static string Base64Padrao(string assinatura)
    {
        var padrao = assinatura.Replace('-', '+').Replace('_', '/');

        return padrao.PadRight(padrao.Length + (4 - padrao.Length % 4) % 4, '=');
    }
}
