using System.Text.RegularExpressions;

namespace OpenTube.Web.Tests.Support;

/// <summary>
/// Apoio para enviar formulários como um navegador faria, incluindo o campo antifalsificação
/// que a aplicação exige em toda escrita.
/// </summary>
public static partial class FormularioHelpers
{
    public static async Task<string> TokenAntifalsificacaoAsync(HttpClient cliente, string caminho)
    {
        var html = await cliente.GetStringAsync(caminho);
        var achado = TokenRegex().Match(html);

        Assert.True(achado.Success, $"campo antifalsificação não encontrado em {caminho}");

        return achado.Groups[1].Value;
    }

    public static async Task<HttpResponseMessage> EnviarFormularioAsync(
        HttpClient cliente,
        string paginaComFormulario,
        string destino,
        IDictionary<string, string> campos)
    {
        var token = await TokenAntifalsificacaoAsync(cliente, paginaComFormulario);

        var conteudo = new Dictionary<string, string>(campos)
        {
            ["__RequestVerificationToken"] = token
        };

        return await cliente.PostAsync(destino, new FormUrlEncodedContent(conteudo));
    }

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex TokenRegex();
}
