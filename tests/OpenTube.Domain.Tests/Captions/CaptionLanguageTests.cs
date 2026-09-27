using OpenTube.Domain.Captions;

namespace OpenTube.Domain.Tests.Captions;

public class CaptionLanguageTests
{
    [Theory]
    [InlineData("pt-BR", "pt-br")]
    [InlineData(" EN ", "en")]
    [InlineData("zh_Hans", "zh-hans")]
    [InlineData("es-419", "es-419")]
    public void Normaliza_o_codigo(string entrada, string esperado)
    {
        Assert.Equal(esperado, CaptionLanguage.Normalize(entrada));
    }

    [Theory]
    [InlineData("")]
    [InlineData("português")]
    [InlineData("../../etc")]
    [InlineData("p")]
    [InlineData("pt-br-x-y-z")]
    public void Recusa_o_que_nao_e_codigo_de_idioma(string entrada)
    {
        Assert.Throws<ArgumentException>(() => CaptionLanguage.Normalize(entrada));
    }

    [Theory]
    [InlineData("pt-BR", "pt")]
    [InlineData("en", "en")]
    [InlineData("zh-hans", "zh")]
    public void Codigo_da_transcricao_e_so_o_idioma(string entrada, string esperado)
    {
        Assert.Equal(esperado, CaptionLanguage.TranscriptionCode(entrada));
    }

    [Theory]
    [InlineData("pt-br", "Português (Brasil)")]
    [InlineData("en", "English")]
    [InlineData("fr", "Français")]
    public void Nome_do_idioma_na_propria_lingua(string codigo, string esperado)
    {
        Assert.Equal(esperado, CaptionLanguage.DisplayName(codigo));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData(" AUTO ")]
    public void Aceita_o_pedido_de_deteccao_automatica(string entrada)
    {
        Assert.Equal(CaptionLanguage.Auto, CaptionLanguage.Normalize(entrada));
        Assert.True(CaptionLanguage.IsAuto(entrada));
        Assert.Equal("auto", CaptionLanguage.TranscriptionCode(entrada));
    }
}
