using OpenTube.Domain.Media;

namespace OpenTube.Domain.Tests.Media;

public class UploadNamesTests
{
    [Theory]
    [InlineData("Reunião de março.mp4", "Reunião de março")]
    [InlineData("aula.01.introducao.mov", "aula.01.introducao")]
    [InlineData("SEM EXTENSAO", "SEM EXTENSAO")]
    [InlineData("  espaços nas pontas .mkv", "espaços nas pontas")]
    [InlineData("Treinamentos/2026/modulo-1.webm", "modulo-1")]
    [InlineData(@"C:\videos\boas-vindas.mp4", "boas-vindas")]
    [InlineData(".mp4", ".mp4")]
    public void Titulo_e_o_nome_do_arquivo_sem_a_extensao(string arquivo, string titulo) =>
        Assert.Equal(titulo, UploadNames.TitleFromFileName(arquivo));

    [Fact]
    public void Titulo_longo_e_cortado_no_limite()
    {
        var titulo = UploadNames.TitleFromFileName(new string('a', 400) + ".mp4");

        Assert.Equal(UploadNames.MaxTitleLength, titulo.Length);
    }

    [Theory]
    [InlineData("Treinamentos/aula-1.mp4", "Treinamentos")]
    [InlineData("Treinamentos/2026/aula-1.mp4", "Treinamentos")]
    [InlineData(@"Onboarding\boas-vindas.mp4", "Onboarding")]
    [InlineData("/Eventos/abertura.mp4", "Eventos")]
    public void Colecao_e_a_pasta_enviada(string caminho, string colecao) =>
        Assert.Equal(colecao, UploadNames.CollectionFromPath(caminho));

    [Fact]
    public void Nomes_vazios_sao_recusados()
    {
        Assert.ThrowsAny<ArgumentException>(() => UploadNames.TitleFromFileName(" "));
        Assert.ThrowsAny<ArgumentException>(() => UploadNames.CollectionFromPath(""));
    }
}
