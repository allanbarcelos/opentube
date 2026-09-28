// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;

namespace OpenTube.Domain.Tests.Entities;

public class InvitationTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();
    private static readonly Guid Video = Guid.CreateVersion7();

    private static Invitation Criar(InvitationKind tipo = InvitationKind.People, string? nota = null) =>
        Invitation.Create(tipo, GrantTargetType.Video, Video, Admin, Agora, note: nota);

    [Fact]
    public void A_nota_aceita_ate_64_caracteres()
    {
        Assert.Equal(new string('x', 64), Criar(nota: new string('x', 64)).Note);
    }

    [Fact]
    public void A_nota_e_aparada_antes_de_contar()
    {
        Assert.Equal(new string('x', 64), Criar(nota: "  " + new string('x', 64) + "  ").Note);
    }

    [Fact]
    public void Nota_com_mais_de_64_caracteres_e_recusada()
    {
        var erro = Assert.Throws<ArgumentException>(() => Criar(nota: new string('x', 65)));

        Assert.Equal("note", erro.ParamName);
    }

    [Fact]
    public void A_concessao_do_link_guarda_o_token_cifrado()
    {
        var concessao = AccessGrant.ForInvitation(Criar(InvitationKind.Link), GrantSubjectType.Link, "resumo");

        concessao.KeepSealedToken("cifrado");

        Assert.Equal("cifrado", concessao.SealedToken);
    }

    [Fact]
    public void So_o_link_guarda_token()
    {
        var concessao = AccessGrant.ForInvitation(Criar(), GrantSubjectType.User, "ana@barcelos.dev");

        Assert.Throws<InvalidOperationException>(() => concessao.KeepSealedToken("cifrado"));
    }
}
