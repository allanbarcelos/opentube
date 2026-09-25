using OpenTube.Infrastructure.Security;

namespace OpenTube.Infrastructure.Tests.Security;

public class TokenHasherTests
{
    private const string Segredo = "segredo-do-servidor";

    [Fact]
    public void O_mesmo_valor_sempre_produz_o_mesmo_resumo()
    {
        Assert.Equal(TokenHasher.Hash("123456", Segredo), TokenHasher.Hash("123456", Segredo));
    }

    [Fact]
    public void Valores_diferentes_produzem_resumos_diferentes()
    {
        Assert.NotEqual(TokenHasher.Hash("123456", Segredo), TokenHasher.Hash("123457", Segredo));
    }

    [Fact]
    public void Trocar_o_segredo_invalida_todos_os_resumos()
    {
        Assert.NotEqual(TokenHasher.Hash("123456", Segredo), TokenHasher.Hash("123456", "outro-segredo"));
    }

    [Fact]
    public void O_resumo_nao_revela_o_valor_original()
    {
        var resumo = TokenHasher.Hash("123456", Segredo);

        Assert.DoesNotContain("123456", resumo);
        Assert.Equal(44, resumo.Length);
    }

    [Fact]
    public void Confere_o_valor_correto()
    {
        var resumo = TokenHasher.Hash("123456", Segredo);

        Assert.True(TokenHasher.Verify("123456", resumo, Segredo));
    }

    [Theory]
    [InlineData("123457")]
    [InlineData("12345")]
    [InlineData("")]
    [InlineData(null)]
    public void Recusa_valor_errado(string? tentativa)
    {
        var resumo = TokenHasher.Hash("123456", Segredo);

        Assert.False(TokenHasher.Verify(tentativa!, resumo, Segredo));
    }

    [Fact]
    public void Recusa_conferencia_contra_resumo_vazio()
    {
        Assert.False(TokenHasher.Verify("123456", "", Segredo));
    }

    [Fact]
    public void Recusa_com_o_segredo_errado()
    {
        var resumo = TokenHasher.Hash("123456", Segredo);

        Assert.False(TokenHasher.Verify("123456", resumo, "segredo-errado"));
    }

    [Theory]
    [InlineData("", Segredo)]
    [InlineData("valor", "")]
    public void Exige_valor_e_segredo(string valor, string segredo)
    {
        Assert.ThrowsAny<ArgumentException>(() => TokenHasher.Hash(valor, segredo));
    }
}
