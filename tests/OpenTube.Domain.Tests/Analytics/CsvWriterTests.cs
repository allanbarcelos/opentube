using System.Globalization;
using System.Text;
using OpenTube.Shared.Analytics;

namespace OpenTube.Domain.Tests.Analytics;

public class CsvWriterTests
{
    [Fact]
    public void Monta_o_cabecalho_e_as_linhas()
    {
        var csv = CsvWriter.Build(["Nome", "Visualizações"], [["Allan", 3], ["Outra", 1]]);

        Assert.Equal("Nome;Visualizações\r\nAllan;3\r\nOutra;1\r\n".Replace("\r\n", Environment.NewLine), csv);
    }

    [Fact]
    public void Protege_o_campo_que_contem_o_separador()
    {
        var csv = CsvWriter.Build(["Título"], [["Reunião; parte 2"]]);

        Assert.Contains("\"Reunião; parte 2\"", csv);
    }

    [Fact]
    public void Duplica_as_aspas_internas()
    {
        var csv = CsvWriter.Build(["Título"], [["Chamado de \"urgente\""]]);

        Assert.Contains("\"Chamado de \"\"urgente\"\"\"", csv);
    }

    [Fact]
    public void Protege_o_campo_com_quebra_de_linha()
    {
        var csv = CsvWriter.Build(["Nota"], [["primeira\nsegunda"]]);

        Assert.Contains("\"primeira\nsegunda\"", csv);
    }

    [Fact]
    public void Campo_vazio_vira_texto_vazio()
    {
        var csv = CsvWriter.Build(["A", "B"], [[null, "x"]]);

        Assert.Contains(";x", csv);
    }

    [Fact]
    public void O_numero_sai_com_virgula_decimal()
    {
        // É o que a planilha em português reconhece como número.
        var csv = CsvWriter.Build(["Minutos"], [[12.5d]]);

        Assert.Contains("12,5", csv);
    }

    [Fact]
    public void O_booleano_sai_em_portugues()
    {
        var anterior = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("pt-BR");

        try
        {
            var csv = CsvWriter.Build(["Concluiu"], [[true], [false]]);

            Assert.Contains("sim", csv);
            Assert.Contains("não", csv);
        }
        finally
        {
            CultureInfo.CurrentUICulture = anterior;
        }
    }

    [Fact]
    public void A_data_sai_no_formato_do_pais()
    {
        var csv = CsvWriter.Build(["Dia"], [[new DateOnly(2026, 9, 24)]]);

        Assert.Contains("24/09/2026", csv);
    }

    [Fact]
    public void Sem_linhas_o_arquivo_traz_so_o_cabecalho()
    {
        var csv = CsvWriter.Build(["A", "B"], []);

        Assert.Equal("A;B" + Environment.NewLine, csv);
    }

    [Fact]
    public void Os_bytes_levam_a_marca_de_ordem()
    {
        var bytes = CsvWriter.ToBytes("A;B");

        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
    }

    [Fact]
    public void Exige_cabecalho_e_linhas()
    {
        Assert.Throws<ArgumentNullException>(() => CsvWriter.Build(null!, []));
        Assert.Throws<ArgumentNullException>(() => CsvWriter.Build(["A"], null!));
    }
}
