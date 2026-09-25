using OpenTube.Domain.Access;
using OpenTube.Domain.Entities;
using OpenTube.Domain.ValueObjects;

namespace OpenTube.Domain.Tests.Access;

public class ViewerTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Visitante_anonimo_nao_tem_identidade()
    {
        Assert.False(Viewer.Anonymous.IsAuthenticated);
        Assert.False(Viewer.Anonymous.IsAdmin);
        Assert.Null(Viewer.Anonymous.Email);
        Assert.Null(Viewer.Anonymous.LinkToken);
    }

    [Fact]
    public void Espectador_autenticado_carrega_email_e_dominio()
    {
        var id = Guid.CreateVersion7();

        var viewer = Viewer.Authenticated(id, EmailAddress.Parse("Allan@Barcelos.dev"));

        Assert.True(viewer.IsAuthenticated);
        Assert.Equal(id, viewer.UserId);
        Assert.Equal("allan@barcelos.dev", viewer.Email);
        Assert.Equal("barcelos.dev", viewer.EmailDomain);
    }

    [Fact]
    public void Deriva_o_espectador_a_partir_do_usuario()
    {
        var user = User.Create(EmailAddress.Parse("admin@opentube.org"), Agora, isAdmin: true);

        var viewer = Viewer.From(user);

        Assert.Equal(user.Id, viewer.UserId);
        Assert.True(viewer.IsAdmin);
        Assert.Equal("opentube.org", viewer.EmailDomain);
    }

    [Fact]
    public void Visitante_com_link_secreto_continua_anonimo()
    {
        var viewer = Viewer.WithLink("  tok_abc  ");

        Assert.False(viewer.IsAuthenticated);
        Assert.Equal("tok_abc", viewer.LinkToken);
    }

    [Fact]
    public void Link_em_branco_e_descartado()
    {
        Assert.Null(Viewer.WithLink("   ").LinkToken);
    }

    [Fact]
    public void Acrescenta_link_a_quem_ja_esta_identificado()
    {
        var viewer = Viewer.Authenticated(Guid.CreateVersion7(), EmailAddress.Parse("a@b.com"))
            .PresentingLink("tok_1");

        Assert.True(viewer.IsAuthenticated);
        Assert.Equal("tok_1", viewer.LinkToken);
    }

    [Fact]
    public void Link_em_branco_nao_apaga_o_token_ja_apresentado()
    {
        var viewer = Viewer.WithLink("tok_1").PresentingLink("   ");

        Assert.Equal("tok_1", viewer.LinkToken);
    }
}
