// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Entities;

namespace OpenTube.Domain.Tests.Entities;

public class AuthSessionTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Duracao = TimeSpan.FromDays(30);
    private static readonly Guid Usuario = Guid.CreateVersion7();

    private static AuthSession Nova() => AuthSession.Open(Usuario, Agora, Duracao, "resumo-ip", "Mozilla/5.0");

    [Fact]
    public void Nasce_valida_pelo_prazo_configurado()
    {
        var sessao = Nova();

        Assert.True(sessao.IsValidAt(Agora));
        Assert.True(sessao.IsValidAt(Agora.AddDays(29)));
        Assert.False(sessao.IsValidAt(Agora.AddDays(30)));
        Assert.Equal(Usuario, sessao.UserId);
    }

    [Fact]
    public void Exige_duracao_positiva()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AuthSession.Open(Usuario, Agora, TimeSpan.Zero));
    }

    [Fact]
    public void Trunca_identificacao_de_navegador_absurdamente_longa()
    {
        var sessao = AuthSession.Open(Usuario, Agora, Duracao, userAgent: new string('x', 900));

        Assert.Equal(400, sessao.UserAgent!.Length);
    }

    [Fact]
    public void Navegador_em_branco_vira_nulo()
    {
        Assert.Null(AuthSession.Open(Usuario, Agora, Duracao, userAgent: "   ").UserAgent);
    }

    [Fact]
    public void Renovar_estende_a_validade_a_partir_do_uso()
    {
        var sessao = Nova();
        var depois = Agora.AddDays(20);

        sessao.Touch(depois, Duracao);

        Assert.Equal(depois, sessao.LastSeenAt);
        Assert.Equal(depois + Duracao, sessao.ExpiresAt);
    }

    [Fact]
    public void Nao_renova_sessao_vencida()
    {
        var sessao = Nova();

        Assert.Throws<InvalidOperationException>(() => sessao.Touch(Agora.AddDays(31), Duracao));
    }

    [Fact]
    public void Encerrar_invalida_na_hora()
    {
        var sessao = Nova();

        sessao.Revoke(Agora);

        Assert.True(sessao.IsRevoked);
        Assert.False(sessao.IsValidAt(Agora));
        Assert.Throws<InvalidOperationException>(() => sessao.Touch(Agora, Duracao));
    }

    [Fact]
    public void Encerrar_duas_vezes_mantem_a_data_original()
    {
        var sessao = Nova();
        sessao.Revoke(Agora);

        sessao.Revoke(Agora.AddDays(1));

        Assert.Equal(Agora, sessao.RevokedAt);
    }
}
