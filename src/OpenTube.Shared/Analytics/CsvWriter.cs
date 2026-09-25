using System.Globalization;
using System.Text;

namespace OpenTube.Shared.Analytics;

/// <summary>
/// Monta arquivos CSV para exportação. Usa ponto e vírgula e marca de ordem de bytes porque o
/// destino real desses arquivos é uma planilha aberta em português, que de outro modo junta
/// tudo numa coluna só e estraga os acentos.
/// </summary>
public static class CsvWriter
{
    public const string ContentType = "text/csv; charset=utf-8";

    private const char Separador = ';';

    public static string Build(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        var texto = new StringBuilder();

        texto.AppendLine(string.Join(Separador, headers.Select(Escapar)));

        foreach (var linha in rows)
            texto.AppendLine(string.Join(Separador, linha.Select(Formatar).Select(Escapar)));

        return texto.ToString();
    }

    /// <summary>Bytes do arquivo, com a marca de ordem que a planilha espera.</summary>
    public static byte[] ToBytes(string content) =>
        [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(content ?? string.Empty)];

    private static string Formatar(object? valor) => valor switch
    {
        null => string.Empty,
        bool booleano => booleano ? "sim" : "não",
        DateTimeOffset momento => momento.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
        DateOnly dia => dia.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
        // Vírgula decimal: é o que a planilha em português reconhece como número.
        double numero => numero.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")),
        _ => valor.ToString() ?? string.Empty
    };

    private static string Escapar(string valor)
    {
        var texto = valor ?? string.Empty;

        // O campo que contém o separador, aspas ou quebra de linha precisa ir entre aspas,
        // com as aspas internas duplicadas.
        if (!texto.Contains(Separador) && !texto.Contains('"') && !texto.Contains('\n') && !texto.Contains('\r'))
            return texto;

        return "\"" + texto.Replace("\"", "\"\"") + "\"";
    }
}
