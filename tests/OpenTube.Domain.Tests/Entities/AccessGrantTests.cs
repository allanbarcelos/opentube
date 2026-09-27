// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;

namespace OpenTube.Domain.Tests.Entities;

public class AccessGrantTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();
    private static readonly Guid Video = Guid.CreateVersion7();
    private static readonly EmailAddress Allan = EmailAddress.Parse("allan@barcelos.dev");

    private static AccessGrant ParaUsuario(
        DateTimeOffset? expira = null, TimeSpan? prazo = null) =>
        AccessGrant.ForUser(Allan, GrantTargetType.Video, Video, Admin, Agora, expira, prazo);

    [Fact]
    public void Concessao_para_pessoa_nasce_valendo_e_sem_prazo()
    {
        var concessao = ParaUsuario();

        Assert.Equal(GrantSubjectType.User, concessao.SubjectType);
        Assert.Equal("allan@barcelos.dev", concessao.SubjectValue);
        Assert.True(concessao.IsActiveAt(Agora));
        Assert.True(concessao.IsActiveAt(Agora.AddYears(50)));
        Assert.Null(concessao.EffectiveExpiry);
    }

    [Fact]
    public void Normaliza_o_email_e_o_dominio_do_sujeito()
    {
        var pessoa = AccessGrant.ForUser(EmailAddress.Parse("Allan@Barcelos.DEV"), GrantTargetType.All, null, Admin, Agora);
        var dominio = AccessGrant.ForDomain("  BARCELOS.DEV ", GrantTargetType.All, null, Admin, Agora);

        Assert.Equal("allan@barcelos.dev", pessoa.SubjectValue);
        Assert.Equal("barcelos.dev", dominio.SubjectValue);
    }

    [Fact]
    public void O_resumo_do_token_do_link_nao_e_alterado()
    {
        // Baixar a caixa do resumo o corromperia: ele é comparado byte a byte.
        var concessao = AccessGrant.ForLink("AbC123+/=", GrantTargetType.Video, Video, Admin, Agora);

        Assert.Equal("AbC123+/=", concessao.SubjectValue);
    }

    [Fact]
    public void Acesso_publico_nao_tem_sujeito()
    {
        var concessao = AccessGrant.Create(GrantSubjectType.Public, null, GrantTargetType.All, null, Admin, Agora);

        Assert.Equal(string.Empty, concessao.SubjectValue);
        Assert.True(concessao.MatchesSubject(GrantSubjectType.Public, null));
    }

    [Theory]
    [InlineData(GrantSubjectType.User)]
    [InlineData(GrantSubjectType.Domain)]
    [InlineData(GrantSubjectType.Link)]
    public void Exige_o_sujeito_quando_ele_faz_sentido(GrantSubjectType tipo)
    {
        Assert.Throws<ArgumentException>(() =>
            AccessGrant.Create(tipo, "  ", GrantTargetType.All, null, Admin, Agora));
    }

    [Fact]
    public void Concessao_para_todo_o_acervo_nao_aponta_para_um_alvo()
    {
        Assert.Throws<ArgumentException>(() =>
            AccessGrant.ForUser(Allan, GrantTargetType.All, Video, Admin, Agora));
    }

    [Theory]
    [InlineData(GrantTargetType.Video)]
    [InlineData(GrantTargetType.Collection)]
    public void Concessao_sobre_alvo_especifico_exige_o_alvo(GrantTargetType tipo)
    {
        Assert.Throws<ArgumentException>(() => AccessGrant.ForUser(Allan, tipo, null, Admin, Agora));
        Assert.Throws<ArgumentException>(() => AccessGrant.ForUser(Allan, tipo, Guid.Empty, Admin, Agora));
    }

    [Fact]
    public void Recusa_validade_que_termina_antes_de_comecar()
    {
        Assert.Throws<ArgumentException>(() => AccessGrant.Create(
            GrantSubjectType.User, Allan.Value, GrantTargetType.All, null, Admin, Agora,
            startsAt: Agora.AddDays(10), expiresAt: Agora.AddDays(5)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Recusa_prazo_relativo_nao_positivo(int dias)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ParaUsuario(prazo: TimeSpan.FromDays(dias)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Recusa_limite_de_visualizacoes_nao_positivo(int limite)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AccessGrant.Create(
            GrantSubjectType.User, Allan.Value, GrantTargetType.All, null, Admin, Agora, maxViews: limite));
    }

    [Fact]
    public void Concessao_agendada_so_vale_a_partir_da_data()
    {
        var concessao = AccessGrant.Create(
            GrantSubjectType.User, Allan.Value, GrantTargetType.All, null, Admin, Agora,
            startsAt: Agora.AddDays(7));

        Assert.False(concessao.IsActiveAt(Agora));
        Assert.True(concessao.IsActiveAt(Agora.AddDays(8)));
    }

    [Fact]
    public void Concessao_com_data_de_fim_deixa_de_valer()
    {
        var concessao = ParaUsuario(expira: Agora.AddDays(30));

        Assert.True(concessao.IsActiveAt(Agora.AddDays(29)));
        Assert.False(concessao.IsActiveAt(Agora.AddDays(30)));
    }

    [Fact]
    public void O_prazo_relativo_so_comeca_a_contar_no_primeiro_uso()
    {
        var concessao = ParaUsuario(prazo: TimeSpan.FromDays(30));

        // Enquanto ninguém abriu o convite, o acesso continua disponível.
        Assert.True(concessao.IsActiveAt(Agora.AddYears(1)));

        concessao.RegisterUse(Agora.AddYears(1));

        Assert.True(concessao.IsActiveAt(Agora.AddYears(1).AddDays(29)));
        Assert.False(concessao.IsActiveAt(Agora.AddYears(1).AddDays(31)));
    }

    [Fact]
    public void O_primeiro_uso_nao_e_sobrescrito_pelos_seguintes()
    {
        var concessao = ParaUsuario(prazo: TimeSpan.FromDays(30));

        concessao.RegisterUse(Agora);
        concessao.RegisterUse(Agora.AddDays(5));

        Assert.Equal(Agora, concessao.FirstUsedAt);
        Assert.Equal(Agora.AddDays(30), concessao.EffectiveExpiry);
        Assert.Equal(2, concessao.ViewsUsed);
    }

    [Fact]
    public void Havendo_data_e_prazo_vale_o_que_terminar_primeiro()
    {
        var concessao = ParaUsuario(expira: Agora.AddDays(10), prazo: TimeSpan.FromDays(30));

        concessao.RegisterUse(Agora);

        // O prazo relativo não pode esticar um acesso que já tinha data de fim.
        Assert.Equal(Agora.AddDays(10), concessao.EffectiveExpiry);
        Assert.False(concessao.IsActiveAt(Agora.AddDays(11)));
    }

    [Fact]
    public void Prazo_relativo_menor_que_a_data_fixa_encurta_o_acesso()
    {
        var concessao = ParaUsuario(expira: Agora.AddDays(90), prazo: TimeSpan.FromDays(7));

        concessao.RegisterUse(Agora.AddDays(1));

        Assert.Equal(Agora.AddDays(8), concessao.EffectiveExpiry);
    }

    [Fact]
    public void Limite_de_visualizacoes_esgota_a_concessao()
    {
        var concessao = AccessGrant.ForLink("resumo", GrantTargetType.Video, Video, Admin, Agora, maxViews: 3);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(concessao.IsActiveAt(Agora));
            concessao.RegisterUse(Agora);
        }

        Assert.True(concessao.IsExhausted);
        Assert.False(concessao.IsActiveAt(Agora));
    }

    [Fact]
    public void Revogar_corta_o_acesso_na_hora()
    {
        var concessao = ParaUsuario();

        concessao.Revoke(Agora);

        Assert.True(concessao.IsRevoked);
        Assert.False(concessao.IsActiveAt(Agora));
    }

    [Fact]
    public void Revogar_duas_vezes_mantem_a_data_original()
    {
        var concessao = ParaUsuario();
        concessao.Revoke(Agora);

        concessao.Revoke(Agora.AddDays(1));

        Assert.Equal(Agora, concessao.RevokedAt);
    }

    [Fact]
    public void Restaurar_devolve_a_concessao_revogada()
    {
        var concessao = ParaUsuario();
        concessao.Revoke(Agora);

        concessao.Restore();

        Assert.True(concessao.IsActiveAt(Agora));
    }

    [Fact]
    public void Reagendar_altera_a_validade()
    {
        var concessao = ParaUsuario(expira: Agora.AddDays(10));

        concessao.Reschedule(null, Agora.AddDays(60), null);

        Assert.True(concessao.IsActiveAt(Agora.AddDays(30)));
        Assert.False(concessao.IsActiveAt(Agora.AddDays(61)));
    }

    [Fact]
    public void Reagendar_recusa_janela_invertida()
    {
        var concessao = ParaUsuario();

        Assert.Throws<ArgumentException>(() =>
            concessao.Reschedule(Agora.AddDays(10), Agora.AddDays(5), null));
    }

    [Fact]
    public void Reagendar_recusa_prazo_nao_positivo()
    {
        var concessao = ParaUsuario();

        Assert.Throws<ArgumentOutOfRangeException>(() => concessao.Reschedule(null, null, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("allan@barcelos.dev", true)]
    [InlineData("ALLAN@BARCELOS.DEV", true)]
    [InlineData("  allan@barcelos.dev  ", true)]
    [InlineData("outro@barcelos.dev", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Reconhece_o_email_do_sujeito(string? email, bool esperado)
    {
        Assert.Equal(esperado, ParaUsuario().MatchesSubject(GrantSubjectType.User, email));
    }

    [Fact]
    public void Concessao_de_pessoa_nao_casa_com_pedido_de_dominio()
    {
        Assert.False(ParaUsuario().MatchesSubject(GrantSubjectType.Domain, "barcelos.dev"));
    }

    [Fact]
    public void Concessao_de_video_alcanca_apenas_aquele_video()
    {
        var concessao = ParaUsuario();

        Assert.True(concessao.Covers(Video, []));
        Assert.False(concessao.Covers(Guid.CreateVersion7(), []));
    }

    [Fact]
    public void Concessao_de_colecao_alcanca_os_videos_da_colecao()
    {
        var colecao = Guid.CreateVersion7();
        var concessao = AccessGrant.ForUser(Allan, GrantTargetType.Collection, colecao, Admin, Agora);

        Assert.True(concessao.Covers(Video, [colecao]));
        Assert.False(concessao.Covers(Video, [Guid.CreateVersion7()]));
        Assert.False(concessao.Covers(Video, []));
    }

    [Fact]
    public void Concessao_de_acervo_alcanca_qualquer_video()
    {
        var concessao = AccessGrant.ForUser(Allan, GrantTargetType.All, null, Admin, Agora);

        Assert.True(concessao.Covers(Guid.CreateVersion7(), []));
    }

    [Fact]
    public void Guarda_a_anotacao_do_administrador_sem_espacos()
    {
        var concessao = ParaUsuario();

        concessao.SetNote("  auditoria externa  ");
        Assert.Equal("auditoria externa", concessao.Note);

        concessao.SetNote("   ");
        Assert.Null(concessao.Note);
    }

    [Fact]
    public void Download_e_negado_por_padrao()
    {
        var concessao = ParaUsuario();

        Assert.False(concessao.CanDownload);

        concessao.AllowDownload(true);
        Assert.True(concessao.CanDownload);
    }
}
