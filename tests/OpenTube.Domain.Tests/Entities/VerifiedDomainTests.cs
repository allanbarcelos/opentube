using OpenTube.Domain.Entities;

namespace OpenTube.Domain.Tests.Entities;

public class VerifiedDomainTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();

    private static VerifiedDomain Novo(string nome = "barcelos.dev") =>
        VerifiedDomain.Register(nome, "tok-abc123", Admin, Agora);

    [Fact]
    public void Nasce_sem_verificacao_e_com_a_porta_no_proprio_dominio()
    {
        var dominio = Novo();

        Assert.False(dominio.IsVerified);
        Assert.False(dominio.EntryAvailable);
        Assert.True(dominio.IsActive);
        Assert.Equal("barcelos.dev", dominio.EntrySlug);
    }

    [Theory]
    [InlineData("  BARCELOS.DEV  ", "barcelos.dev")]
    [InlineData("barcelos.dev.", "barcelos.dev")]
    [InlineData("Sub.Empresa.COM.BR", "sub.empresa.com.br")]
    public void Normaliza_o_nome_do_dominio(string entrada, string esperado)
    {
        Assert.Equal(esperado, Novo(entrada).Name);
    }

    [Theory]
    [InlineData("sem-ponto")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("espaço.com")]
    [InlineData(".com")]
    [InlineData("dominio..com")]
    [InlineData("allan@barcelos.dev")]
    public void Recusa_nome_que_nao_e_dominio(string nome)
    {
        Assert.Throws<ArgumentException>(() => Novo(nome));
    }

    [Fact]
    public void Exige_o_token_de_verificacao()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            VerifiedDomain.Register("barcelos.dev", "  ", Admin, Agora));
    }

    [Fact]
    public void Indica_o_registro_que_precisa_ser_publicado()
    {
        var dominio = Novo();

        Assert.Equal("_opentube-verify.barcelos.dev", dominio.VerificationRecordName);
        Assert.Equal("opentube-verify=tok-abc123", dominio.ExpectedRecordValue);
    }

    [Theory]
    [InlineData("opentube-verify=tok-abc123")]
    [InlineData("OPENTUBE-VERIFY=TOK-ABC123")]
    [InlineData("  opentube-verify=tok-abc123  ")]
    [InlineData("\"opentube-verify=tok-abc123\"")]
    [InlineData("tok-abc123")]
    public void Aceita_as_formas_em_que_os_provedores_devolvem_o_registro(string registro)
    {
        Assert.True(Novo().Matches([registro]));
    }

    [Fact]
    public void Encontra_o_registro_no_meio_de_outros()
    {
        var dominio = Novo();

        Assert.True(dominio.Matches(["v=spf1 include:exemplo.com ~all", "", "opentube-verify=tok-abc123"]));
    }

    [Theory]
    [InlineData("opentube-verify=outro-token")]
    [InlineData("tok-abc124")]
    [InlineData("")]
    public void Recusa_registro_que_nao_confere(string registro)
    {
        Assert.False(Novo().Matches([registro]));
    }

    [Fact]
    public void Sem_nenhum_registro_nao_verifica()
    {
        Assert.False(Novo().Matches([]));
    }

    [Fact]
    public void A_porta_de_entrada_abre_depois_da_verificacao()
    {
        var dominio = Novo();

        dominio.MarkVerified(Agora);

        Assert.True(dominio.IsVerified);
        Assert.True(dominio.EntryAvailable);
        Assert.Equal(Agora, dominio.VerifiedAt);
    }

    [Fact]
    public void Verificar_duas_vezes_mantem_a_data_original()
    {
        var dominio = Novo();
        dominio.MarkVerified(Agora);

        dominio.MarkVerified(Agora.AddDays(1));

        Assert.Equal(Agora, dominio.VerifiedAt);
    }

    [Fact]
    public void Reemitir_o_token_derruba_a_verificacao()
    {
        var dominio = Novo();
        dominio.MarkVerified(Agora);

        dominio.ResetVerification("tok-novo");

        Assert.False(dominio.IsVerified);
        Assert.False(dominio.EntryAvailable);
        Assert.Equal("opentube-verify=tok-novo", dominio.ExpectedRecordValue);
    }

    [Fact]
    public void Desligar_a_porta_nao_desfaz_a_verificacao()
    {
        var dominio = Novo();
        dominio.MarkVerified(Agora);

        dominio.SetEntryEnabled(false);

        Assert.True(dominio.IsVerified);
        Assert.False(dominio.EntryAvailable);
    }

    [Fact]
    public void Desativar_o_dominio_fecha_a_porta()
    {
        var dominio = Novo();
        dominio.MarkVerified(Agora);

        dominio.Disable(Agora);
        Assert.False(dominio.EntryAvailable);

        dominio.Enable();
        Assert.True(dominio.EntryAvailable);
    }

    [Fact]
    public void O_endereco_da_porta_pode_ser_trocado_por_um_nao_adivinhavel()
    {
        var dominio = Novo();

        dominio.ChangeEntrySlug("  X7K2-Privado  ");

        Assert.Equal("x7k2-privado", dominio.EntrySlug);
    }

    [Fact]
    public void O_endereco_da_porta_nao_pode_ficar_vazio()
    {
        Assert.ThrowsAny<ArgumentException>(() => Novo().ChangeEntrySlug("  "));
    }

    [Theory]
    [InlineData("allan@barcelos.dev", true)]
    [InlineData("ALLAN@BARCELOS.DEV", true)]
    [InlineData("  allan@barcelos.dev ", true)]
    [InlineData("allan@outro.com", false)]
    [InlineData("allan@naobarcelos.dev", false)]
    [InlineData("sem-arroba", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Aceita_apenas_endereco_do_proprio_dominio(string? email, bool esperado)
    {
        Assert.Equal(esperado, Novo().Accepts(email));
    }

    [Fact]
    public void Com_lista_de_permitidos_apenas_eles_entram()
    {
        var dominio = Novo();

        dominio.SetAllowedEmails(["  ALLAN@barcelos.dev ", "outro@barcelos.dev", "  "]);

        Assert.Equal(2, dominio.AllowedEmails.Length);
        Assert.True(dominio.Accepts("allan@barcelos.dev"));
        Assert.False(dominio.Accepts("terceiro@barcelos.dev"));
    }

    [Fact]
    public void Limpar_a_lista_devolve_o_comportamento_padrao()
    {
        var dominio = Novo();
        dominio.SetAllowedEmails(["allan@barcelos.dev"]);

        dominio.SetAllowedEmails(null);

        Assert.Empty(dominio.AllowedEmails);
        Assert.True(dominio.Accepts("terceiro@barcelos.dev"));
    }

    [Fact]
    public void Guarda_o_responsavel_e_a_anotacao_normalizados()
    {
        var dominio = VerifiedDomain.Register("barcelos.dev", "tok", Admin, Agora, "  TI@Barcelos.dev ", "  contrato 2026 ");

        Assert.Equal("ti@barcelos.dev", dominio.ContactEmail);
        Assert.Equal("contrato 2026", dominio.Note);

        dominio.SetContact("   ");
        dominio.SetNote("   ");

        Assert.Null(dominio.ContactEmail);
        Assert.Null(dominio.Note);
    }
}
