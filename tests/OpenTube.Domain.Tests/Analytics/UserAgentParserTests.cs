using OpenTube.Domain.Analytics;
using OpenTube.Domain.Enums;

namespace OpenTube.Domain.Tests.Analytics;

public class UserAgentParserTests
{
    private const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
    private const string SafariMac = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15";
    private const string SafariIphone = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";
    private const string ChromeAndroid = "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36";
    private const string Ipad = "Mozilla/5.0 (iPad; CPU OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/604.1";
    private const string Firefox = "Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0";
    private const string Edge = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0";

    [Theory]
    [InlineData(Chrome, DeviceType.Desktop, "Windows", "Chrome")]
    [InlineData(SafariMac, DeviceType.Desktop, "macOS", "Safari")]
    [InlineData(SafariIphone, DeviceType.Mobile, "iOS", "Safari")]
    [InlineData(ChromeAndroid, DeviceType.Mobile, "Android", "Chrome")]
    [InlineData(Ipad, DeviceType.Tablet, "iOS", "Safari")]
    [InlineData(Firefox, DeviceType.Desktop, "Linux", "Firefox")]
    [InlineData(Edge, DeviceType.Desktop, "Windows", "Edge")]
    public void Identifica_os_navegadores_mais_comuns(string ua, DeviceType aparelho, string sistema, string navegador)
    {
        var perfil = UserAgentParser.Parse(ua);

        Assert.Equal(aparelho, perfil.Device);
        Assert.Equal(sistema, perfil.OperatingSystem);
        Assert.Equal(navegador, perfil.Browser);
    }

    [Fact]
    public void O_edge_nao_e_confundido_com_chrome()
    {
        // Quase todo navegador se declara Chrome no caminho; a ordem de teste importa.
        Assert.Equal("Edge", UserAgentParser.Parse(Edge).Browser);
    }

    [Fact]
    public void Tablet_android_nao_e_tratado_como_celular()
    {
        var ua = "Mozilla/5.0 (Linux; Android 14; SM-X200) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

        Assert.Equal(DeviceType.Tablet, UserAgentParser.Parse(ua).Device);
    }

    [Fact]
    public void Televisao_e_reconhecida()
    {
        var ua = "Mozilla/5.0 (SMART-TV; Linux; Tizen 7.0) AppleWebKit/537.36 Chrome/108.0.0.0 Safari/537.36";

        Assert.Equal(DeviceType.Tv, UserAgentParser.Parse(ua).Device);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sem_identificacao_tudo_fica_desconhecido(string? ua)
    {
        var perfil = UserAgentParser.Parse(ua);

        Assert.Equal(DeviceType.Unknown, perfil.Device);
        Assert.Equal(UserAgentParser.Desconhecido, perfil.OperatingSystem);
        Assert.Equal(UserAgentParser.Desconhecido, perfil.Browser);
    }

    [Fact]
    public void Identificacao_estranha_nao_quebra_a_leitura()
    {
        var perfil = UserAgentParser.Parse("robô-qualquer/1.0");

        Assert.Equal(DeviceType.Unknown, perfil.Device);
        Assert.Equal(UserAgentParser.Desconhecido, perfil.Browser);
    }
}
