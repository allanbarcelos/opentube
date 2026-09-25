using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;

namespace OpenTube.Web.Components.Shared.Graficos;

/// <summary>
/// Monta os rótulos de eixo dos gráficos. O Razor reserva a marcação <c>text</c> para o
/// próprio uso, então ela não pode ser escrita direto no componente.
/// </summary>
public static class RotuloDeEixo
{
    private const string Classe = "grafico-rotulo";

    public static MarkupString Criar(double x, double y, string ancora, string valor)
    {
        var texto = "<text class=\"" + Classe + "\""
            + " x=\"" + x.ToString("0.##", CultureInfo.InvariantCulture) + "\""
            + " y=\"" + y.ToString("0.##", CultureInfo.InvariantCulture) + "\""
            + " text-anchor=\"" + WebUtility.HtmlEncode(ancora) + "\">"
            + WebUtility.HtmlEncode(valor)
            + "</text>";

        return new MarkupString(texto);
    }
}
