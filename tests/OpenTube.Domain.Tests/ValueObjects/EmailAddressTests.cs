using OpenTube.Domain.ValueObjects;

namespace OpenTube.Domain.Tests.ValueObjects;

public class EmailAddressTests
{
    [Theory]
    [InlineData("allan@barcelos.dev")]
    [InlineData("a.b+tag@sub.dominio.com.br")]
    [InlineData("nome_sobrenome@empresa.io")]
    [InlineData("x@y.co")]
    public void Aceita_enderecos_validos(string input)
    {
        Assert.True(EmailAddress.TryParse(input, out var email));
        Assert.Equal(input.ToLowerInvariant(), email.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sem-arroba.com")]
    [InlineData("@sem-local.com")]
    [InlineData("sem@dominio")]
    [InlineData("espaco no@meio.com")]
    [InlineData("duplo@@arroba.com")]
    [InlineData("tld@curto.x")]
    public void Rejeita_enderecos_invalidos(string? input)
    {
        Assert.False(EmailAddress.TryParse(input, out _));
    }

    [Fact]
    public void Rejeita_endereco_acima_do_limite_do_protocolo()
    {
        var longo = new string('a', 250) + "@x.com";
        Assert.False(EmailAddress.TryParse(longo, out _));
    }

    [Fact]
    public void Normaliza_caixa_e_espacos_para_nao_criar_duas_identidades()
    {
        var a = EmailAddress.Parse("  Allan@Barcelos.DEV ");
        var b = EmailAddress.Parse("allan@barcelos.dev");

        Assert.Equal("allan@barcelos.dev", a.Value);
        Assert.Equal(b, a);
    }

    [Fact]
    public void Separa_parte_local_e_dominio()
    {
        var email = EmailAddress.Parse("allan@barcelos.dev");

        Assert.Equal("allan", email.LocalPart);
        Assert.Equal("barcelos.dev", email.Domain);
    }

    [Theory]
    [InlineData("barcelos.dev", true)]
    [InlineData("BARCELOS.DEV", true)]
    [InlineData("  barcelos.dev  ", true)]
    [InlineData("outrodominio.dev", false)]
    [InlineData("celos.dev", false)]
    [InlineData("", false)]
    public void Compara_dominio_ignorando_caixa_e_espacos(string dominio, bool esperado)
    {
        var email = EmailAddress.Parse("allan@barcelos.dev");

        Assert.Equal(esperado, email.BelongsTo(dominio));
    }

    [Fact]
    public void Nao_confunde_dominio_com_sufixo_de_outro_dominio()
    {
        var email = EmailAddress.Parse("invasor@naobarcelos.dev");

        Assert.False(email.BelongsTo("barcelos.dev"));
    }

    [Fact]
    public void Parse_lanca_excecao_descritiva_para_endereco_invalido()
    {
        var erro = Assert.Throws<FormatException>(() => EmailAddress.Parse("invalido"));

        Assert.Contains("invalido", erro.Message);
    }
}
