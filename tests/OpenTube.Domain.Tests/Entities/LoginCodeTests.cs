// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;

namespace OpenTube.Domain.Tests.Entities;

public class LoginCodeTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly EmailAddress Email = EmailAddress.Parse("allan@barcelos.dev");

    private static LoginCode Novo(TimeSpan? validade = null) =>
        LoginCode.Issue(Email, AuthPurpose.Login, "resumo-do-codigo", "resumo-do-token", Agora, validade ?? TimeSpan.FromMinutes(15));

    [Fact]
    public void Nasce_utilizavel_e_guarda_apenas_resumos()
    {
        var codigo = Novo();

        Assert.True(codigo.IsUsableAt(Agora));
        Assert.False(codigo.IsConsumed);
        Assert.Equal(0, codigo.Attempts);
        Assert.Equal("allan@barcelos.dev", codigo.Email);
        Assert.Equal("barcelos.dev", codigo.EmailDomain);
        Assert.Equal(Agora.AddMinutes(15), codigo.ExpiresAt);
    }

    [Fact]
    public void Exige_os_resumos_e_uma_validade_positiva()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            LoginCode.Issue(Email, AuthPurpose.Login, "", "token", Agora, TimeSpan.FromMinutes(15)));
        Assert.ThrowsAny<ArgumentException>(() =>
            LoginCode.Issue(Email, AuthPurpose.Login, "codigo", "", Agora, TimeSpan.FromMinutes(15)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LoginCode.Issue(Email, AuthPurpose.Login, "codigo", "token", Agora, TimeSpan.Zero));
    }

    [Fact]
    public void Deixa_de_valer_depois_do_prazo()
    {
        var codigo = Novo();

        Assert.False(codigo.IsExpiredAt(Agora.AddMinutes(14)));
        Assert.True(codigo.IsExpiredAt(Agora.AddMinutes(15)));
        Assert.False(codigo.IsUsableAt(Agora.AddMinutes(16)));
    }

    [Fact]
    public void Queima_depois_de_cinco_erros()
    {
        var codigo = Novo();

        for (var i = 0; i < LoginCode.MaxAttempts; i++)
            codigo.RegisterFailedAttempt();

        Assert.True(codigo.IsExhausted);
        Assert.False(codigo.IsUsableAt(Agora));
    }

    [Fact]
    public void Ainda_vale_na_ultima_tentativa()
    {
        var codigo = Novo();

        for (var i = 0; i < LoginCode.MaxAttempts - 1; i++)
            codigo.RegisterFailedAttempt();

        Assert.True(codigo.IsUsableAt(Agora));
    }

    [Fact]
    public void So_pode_ser_usado_uma_vez()
    {
        var codigo = Novo();

        codigo.Consume(Agora);

        Assert.True(codigo.IsConsumed);
        Assert.False(codigo.IsUsableAt(Agora));
        Assert.Throws<InvalidOperationException>(() => codigo.Consume(Agora));
    }

    [Fact]
    public void Guarda_a_concessao_que_originou_o_convite()
    {
        var concessao = Guid.CreateVersion7();

        var codigo = LoginCode.Issue(Email, AuthPurpose.Invite, "c", "t", Agora, TimeSpan.FromDays(7), grantId: concessao);

        Assert.Equal(concessao, codigo.GrantId);
        Assert.Equal(AuthPurpose.Invite, codigo.Purpose);
    }
}
