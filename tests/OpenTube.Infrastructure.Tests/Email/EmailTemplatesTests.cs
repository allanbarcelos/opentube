using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Email;

namespace OpenTube.Infrastructure.Tests.Email;

public class EmailTemplatesTests
{
    private static EmailMessage Montar(AuthPurpose proposito = AuthPurpose.Login) =>
        EmailTemplates.AccessCode("allan@barcelos.dev", "123456", "https://opentube.org/entrar/tok", proposito, TimeSpan.FromMinutes(15));

    [Fact]
    public void Traz_o_codigo_e_o_link_nas_duas_versoes_do_corpo()
    {
        var mensagem = Montar();

        Assert.Contains("123456", mensagem.TextBody);
        Assert.Contains("123456", mensagem.HtmlBody);
        Assert.Contains("https://opentube.org/entrar/tok", mensagem.TextBody);
        Assert.Contains("https://opentube.org/entrar/tok", mensagem.HtmlBody);
    }

    [Fact]
    public void Informa_a_validade_em_minutos()
    {
        var mensagem = Montar();

        Assert.Contains("15 minutos", mensagem.TextBody);
        Assert.Contains("15 minutos", mensagem.HtmlBody);
    }

    [Fact]
    public void O_convite_tem_assunto_proprio()
    {
        Assert.NotEqual(Montar(AuthPurpose.Login).Subject, Montar(AuthPurpose.Invite).Subject);
        Assert.Contains("acesso a vídeos", Montar(AuthPurpose.Invite).Subject);
    }

    [Fact]
    public void Nao_menciona_senha_em_lugar_nenhum()
    {
        var mensagem = Montar();

        Assert.DoesNotContain("senha", mensagem.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("senha", mensagem.HtmlBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Avisa_quem_nao_pediu_o_acesso()
    {
        Assert.Contains("não pediu", Montar().TextBody);
    }

    [Fact]
    public void Escapa_conteudo_no_corpo_em_html()
    {
        var mensagem = EmailTemplates.AccessCode(
            "a@b.com", "123456", "https://opentube.org/entrar/a\"><script>alert(1)</script>", AuthPurpose.Login, TimeSpan.FromMinutes(15));

        Assert.DoesNotContain("<script>", mensagem.HtmlBody);
        Assert.Contains("&lt;script&gt;", mensagem.HtmlBody);
    }

    [Fact]
    public void O_destinatario_e_o_endereco_informado()
    {
        Assert.Equal("allan@barcelos.dev", Montar().To);
    }
}
