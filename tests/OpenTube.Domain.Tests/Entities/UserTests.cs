using OpenTube.Domain.Entities;
using OpenTube.Domain.ValueObjects;

namespace OpenTube.Domain.Tests.Entities;

public class UserTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Cria_usuario_comum_ativo()
    {
        var user = User.Create(EmailAddress.Parse("allan@barcelos.dev"), Agora);

        Assert.Equal("allan@barcelos.dev", user.Email);
        Assert.Equal("barcelos.dev", user.EmailDomain);
        Assert.False(user.IsAdmin);
        Assert.True(user.IsActive);
        Assert.Null(user.LastSeenAt);
    }

    [Fact]
    public void Guarda_o_dominio_separado_para_consulta_por_dominio()
    {
        var user = User.Create(EmailAddress.Parse("pessoa@sub.empresa.com.br"), Agora);

        Assert.Equal("sub.empresa.com.br", user.EmailDomain);
    }

    [Fact]
    public void Nome_em_branco_vira_nulo()
    {
        var user = User.Create(EmailAddress.Parse("a@b.com"), Agora, displayName: "   ");

        Assert.Null(user.DisplayName);
    }

    [Fact]
    public void Registra_o_ultimo_acesso()
    {
        var user = User.Create(EmailAddress.Parse("a@b.com"), Agora);

        user.Touch(Agora.AddHours(3));

        Assert.Equal(Agora.AddHours(3), user.LastSeenAt);
    }

    [Fact]
    public void Desativa_usuario_comum()
    {
        var user = User.Create(EmailAddress.Parse("a@b.com"), Agora);

        user.Disable(Agora);

        Assert.False(user.IsActive);
        Assert.Equal(Agora, user.DisabledAt);
    }

    [Fact]
    public void Desativar_duas_vezes_mantem_a_data_original()
    {
        var user = User.Create(EmailAddress.Parse("a@b.com"), Agora);
        user.Disable(Agora);

        user.Disable(Agora.AddDays(1));

        Assert.Equal(Agora, user.DisabledAt);
    }

    [Fact]
    public void Nao_desativa_administrador_sem_antes_remover_o_papel()
    {
        var admin = User.Create(EmailAddress.Parse("admin@b.com"), Agora, isAdmin: true);

        Assert.Throws<InvalidOperationException>(() => admin.Disable(Agora));

        admin.RevokeAdmin();
        admin.Disable(Agora);

        Assert.False(admin.IsActive);
    }

    [Fact]
    public void Reativa_usuario()
    {
        var user = User.Create(EmailAddress.Parse("a@b.com"), Agora);
        user.Disable(Agora);

        user.Enable();

        Assert.True(user.IsActive);
        Assert.Null(user.DisabledAt);
    }

    [Fact]
    public void Concede_e_revoga_o_papel_de_administrador()
    {
        var user = User.Create(EmailAddress.Parse("a@b.com"), Agora);

        user.GrantAdmin();
        Assert.True(user.IsAdmin);

        user.RevokeAdmin();
        Assert.False(user.IsAdmin);
    }

    [Fact]
    public void Renomeia_removendo_espacos()
    {
        var user = User.Create(EmailAddress.Parse("a@b.com"), Agora);

        user.Rename("  Allan Barcelos  ");

        Assert.Equal("Allan Barcelos", user.DisplayName);
    }
}
