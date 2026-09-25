using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Email;

namespace OpenTube.Infrastructure.Tests.Email;

public class EmailTemplatesTests
{
    private static EmailMessage Montar(AuthPurpose proposito = AuthPurpose.Login) =>
        EmailTemplates.AccessCode("allan@barcelos.dev", "123456", "https://opentube.org/sign-in/tok", proposito, TimeSpan.FromMinutes(15));

    [Fact]
    public void Traz_o_codigo_e_o_link_nas_duas_versoes_do_corpo()
    {
        var mensagem = Montar();

        Assert.Contains("123456", mensagem.TextBody);
        Assert.Contains("123456", mensagem.HtmlBody);
        Assert.Contains("https://opentube.org/sign-in/tok", mensagem.TextBody);
        Assert.Contains("https://opentube.org/sign-in/tok", mensagem.HtmlBody);
    }

    [Fact]
    public void Informa_a_validade_em_minutos()
    {
        var mensagem = Montar();

        Assert.Contains("15 minutes", mensagem.TextBody);
        Assert.Contains("15 minutes", mensagem.HtmlBody);
    }

    [Fact]
    public void O_convite_tem_assunto_proprio()
    {
        Assert.NotEqual(Montar(AuthPurpose.Login).Subject, Montar(AuthPurpose.Invite).Subject);
        Assert.Contains("access to videos", Montar(AuthPurpose.Invite).Subject);
    }

    [Fact]
    public void Nao_menciona_senha_em_lugar_nenhum()
    {
        var mensagem = Montar();

        Assert.DoesNotContain("password", mensagem.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", mensagem.HtmlBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Avisa_quem_nao_pediu_o_acesso()
    {
        Assert.Contains("did not ask", Montar().TextBody);
    }

    [Fact]
    public void Escapa_conteudo_no_corpo_em_html()
    {
        var mensagem = EmailTemplates.AccessCode(
            "a@b.com", "123456", "https://opentube.org/sign-in/a\"><script>alert(1)</script>", AuthPurpose.Login, TimeSpan.FromMinutes(15));

        Assert.DoesNotContain("<script>", mensagem.HtmlBody);
        Assert.Contains("&lt;script&gt;", mensagem.HtmlBody);
    }

    [Fact]
    public void O_destinatario_e_o_endereco_informado()
    {
        Assert.Equal("allan@barcelos.dev", Montar().To);
    }
}
