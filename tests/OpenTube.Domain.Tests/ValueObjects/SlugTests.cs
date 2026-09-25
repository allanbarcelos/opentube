using OpenTube.Domain.ValueObjects;

namespace OpenTube.Domain.Tests.ValueObjects;

public class SlugTests
{
    [Theory]
    [InlineData("Reunião Trimestral", "reuniao-trimestral")]
    [InlineData("Treinamento: Segurança da Informação", "treinamento-seguranca-da-informacao")]
    [InlineData("  espaços   sobrando  ", "espacos-sobrando")]
    [InlineData("Ação & Reação", "acao-reacao")]
    [InlineData("v2.1 — Notas de versão", "v2-1-notas-de-versao")]
    [InlineData("JÁ-EM-MAIÚSCULAS", "ja-em-maiusculas")]
    public void Converte_titulo_em_endereco_legivel(string titulo, string esperado)
    {
        Assert.Equal(esperado, Slug.From(titulo));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void Devolve_vazio_quando_nao_ha_nada_aproveitavel(string? titulo)
    {
        Assert.Equal(string.Empty, Slug.From(titulo));
    }

    [Fact]
    public void Nao_deixa_hifen_nas_pontas()
    {
        var slug = Slug.From("--- olá ---");

        Assert.Equal("ola", slug);
    }

    [Fact]
    public void Respeita_o_tamanho_maximo()
    {
        var slug = Slug.From(new string('a', 200));

        Assert.Equal(Slug.MaxLength, slug.Length);
    }

    [Fact]
    public void Usa_o_slug_base_quando_esta_livre()
    {
        var slug = Slug.Unique("Reunião Trimestral", _ => false);

        Assert.Equal("reuniao-trimestral", slug);
    }

    [Fact]
    public void Acrescenta_sufixo_numerico_quando_ja_existe()
    {
        var existentes = new HashSet<string> { "reuniao-trimestral", "reuniao-trimestral-2" };

        var slug = Slug.Unique("Reunião Trimestral", existentes.Contains);

        Assert.Equal("reuniao-trimestral-3", slug);
    }

    [Fact]
    public void Usa_termo_generico_quando_o_titulo_nao_gera_slug()
    {
        var slug = Slug.Unique("###", _ => false);

        Assert.Equal("video", slug);
    }

    [Fact]
    public void Cai_para_sufixo_aleatorio_quando_todos_os_numeros_estao_tomados()
    {
        var slug = Slug.Unique("colisao", _ => true);

        Assert.StartsWith("colisao-", slug);
        Assert.Matches("^colisao-[0-9a-f]{8}$", slug);
    }

    [Fact]
    public void Nao_ultrapassa_o_tamanho_maximo_mesmo_com_sufixo()
    {
        var titulo = new string('a', 200);
        var existentes = new HashSet<string> { Slug.From(titulo) };

        var slug = Slug.Unique(titulo, existentes.Contains);

        Assert.True(slug.Length <= Slug.MaxLength, $"slug com {slug.Length} caracteres");
    }

    [Fact]
    public void Exige_a_funcao_de_verificacao()
    {
        Assert.Throws<ArgumentNullException>(() => Slug.Unique("teste", null!));
    }
}
