// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Access;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;

namespace OpenTube.Domain.Tests.Access;

/// <summary>
/// A matriz de acesso a vídeos restritos. É o ponto do sistema em que um erro publica
/// conteúdo confidencial, então cada combinação de sujeito, alvo e validade é exercitada.
/// </summary>
public class AccessPolicyComConcessoesTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();
    private static readonly EmailAddress Allan = EmailAddress.Parse("allan@barcelos.dev");

    private static readonly Viewer Convidado =
        Viewer.Authenticated(Guid.CreateVersion7(), Allan);

    private static readonly Viewer Estranho =
        Viewer.Authenticated(Guid.CreateVersion7(), EmailAddress.Parse("estranho@outra.com"));

    private static Video Restrito()
    {
        var video = Video.CreateDraft("Confidencial", "confidencial", "originals/a.mp4", Admin, Agora);
        video.MarkUploaded(1024);
        video.StartProcessing();
        video.MarkReady("vod/a/", 120, 1280, 720, null, null, Agora);
        video.ChangeVisibility(VideoVisibility.Restricted);

        return video;
    }

    private static AccessDecision Avaliar(Viewer viewer, Video video, IReadOnlyCollection<AccessGrant> concessoes,
        IReadOnlyCollection<Guid>? colecoes = null, DateTimeOffset? quando = null) =>
        AccessPolicy.Evaluate(viewer, video, concessoes, colecoes ?? [], quando ?? Agora);

    [Fact]
    public void Concessao_para_a_pessoa_libera_o_video()
    {
        var video = Restrito();
        var concessao = AccessGrant.ForUser(Allan, GrantTargetType.Video, video.Id, Admin, Agora);

        var decisao = Avaliar(Convidado, video, [concessao]);

        Assert.True(decisao.Allowed);
        Assert.Equal(AccessReason.GrantedToUser, decisao.Reason);
    }

    [Fact]
    public void Concessao_de_uma_pessoa_nao_serve_para_outra()
    {
        var video = Restrito();
        var concessao = AccessGrant.ForUser(Allan, GrantTargetType.Video, video.Id, Admin, Agora);

        var decisao = Avaliar(Estranho, video, [concessao]);

        Assert.False(decisao.Allowed);
        Assert.Equal(AccessReason.NoGrant, decisao.Reason);
    }

    [Fact]
    public void Concessao_por_dominio_libera_qualquer_email_do_dominio()
    {
        var video = Restrito();
        var concessao = AccessGrant.ForDomain("barcelos.dev", GrantTargetType.Video, video.Id, Admin, Agora);

        var decisao = Avaliar(Convidado, video, [concessao]);

        Assert.True(decisao.Allowed);
        Assert.Equal(AccessReason.GrantedToDomain, decisao.Reason);
    }

    [Fact]
    public void Concessao_por_dominio_nao_alcanca_dominio_parecido()
    {
        var video = Restrito();
        var concessao = AccessGrant.ForDomain("barcelos.dev", GrantTargetType.Video, video.Id, Admin, Agora);
        var vizinho = Viewer.Authenticated(Guid.CreateVersion7(), EmailAddress.Parse("alguem@naobarcelos.dev"));

        Assert.False(Avaliar(vizinho, video, [concessao]).Allowed);
    }

    [Fact]
    public void Visitante_anonimo_nao_aproveita_concessao_de_dominio()
    {
        var video = Restrito();
        var concessao = AccessGrant.ForDomain("barcelos.dev", GrantTargetType.Video, video.Id, Admin, Agora);

        Assert.False(Avaliar(Viewer.Anonymous, video, [concessao]).Allowed);
    }

    [Fact]
    public void Concessao_por_link_libera_quem_comprovou_ter_o_token()
    {
        var video = Restrito();
        var concessao = AccessGrant.ForLink("resumo-do-token", GrantTargetType.Video, video.Id, Admin, Agora);

        var decisao = Avaliar(Viewer.WithLink(concessao.Id), video, [concessao]);

        Assert.True(decisao.Allowed);
        Assert.Equal(AccessReason.GrantedByLink, decisao.Reason);
    }

    [Fact]
    public void Concessao_por_link_nao_libera_quem_nao_apresentou_o_token()
    {
        var video = Restrito();
        var concessao = AccessGrant.ForLink("resumo-do-token", GrantTargetType.Video, video.Id, Admin, Agora);

        Assert.False(Avaliar(Viewer.Anonymous, video, [concessao]).Allowed);
        Assert.False(Avaliar(Convidado, video, [concessao]).Allowed);
    }

    [Fact]
    public void Um_link_nao_abre_o_video_de_outra_concessao()
    {
        var video = Restrito();
        var doVideo = AccessGrant.ForLink("resumo-a", GrantTargetType.Video, video.Id, Admin, Agora);
        var deOutroVideo = AccessGrant.ForLink("resumo-b", GrantTargetType.Video, Guid.CreateVersion7(), Admin, Agora);

        Assert.False(Avaliar(Viewer.WithLink(deOutroVideo.Id), video, [doVideo, deOutroVideo]).Allowed);
    }

    [Fact]
    public void Concessao_publica_libera_ate_visitante_anonimo()
    {
        var video = Restrito();
        var concessao = AccessGrant.Create(GrantSubjectType.Public, null, GrantTargetType.Video, video.Id, Admin, Agora);

        Assert.True(Avaliar(Viewer.Anonymous, video, [concessao]).Allowed);
    }

    [Fact]
    public void Concessao_de_colecao_alcanca_os_videos_dela()
    {
        var video = Restrito();
        var colecao = Guid.CreateVersion7();
        var concessao = AccessGrant.ForUser(Allan, GrantTargetType.Collection, colecao, Admin, Agora);

        Assert.True(Avaliar(Convidado, video, [concessao], [colecao]).Allowed);
        Assert.False(Avaliar(Convidado, video, [concessao], []).Allowed);
    }

    [Fact]
    public void Video_acrescentado_depois_herda_a_concessao_da_colecao()
    {
        // É a razão de existir da coleção: liberar o conjunto, e não uma lista congelada.
        var colecao = Guid.CreateVersion7();
        var concessao = AccessGrant.ForUser(Allan, GrantTargetType.Collection, colecao, Admin, Agora);
        var novoVideo = Restrito();

        Assert.True(Avaliar(Convidado, novoVideo, [concessao], [colecao]).Allowed);
    }

    [Fact]
    public void Concessao_de_acervo_inteiro_alcanca_qualquer_video()
    {
        var video = Restrito();
        var concessao = AccessGrant.ForUser(Allan, GrantTargetType.All, null, Admin, Agora);

        Assert.True(Avaliar(Convidado, video, [concessao]).Allowed);
    }

    [Fact]
    public void Concessao_expirada_e_negada_com_o_motivo_a_vista()
    {
        var video = Restrito();
        var concessao = AccessGrant.ForUser(Allan, GrantTargetType.Video, video.Id, Admin, Agora, expiresAt: Agora.AddDays(7));

        var decisao = Avaliar(Convidado, video, [concessao], quando: Agora.AddDays(8));

        Assert.False(decisao.Allowed);
        // Dizer "seu acesso expirou" é muito mais útil que um "não encontrado" genérico.
        Assert.Equal(AccessReason.GrantExpired, decisao.Reason);
    }

    [Fact]
    public void Concessao_ainda_nao_iniciada_informa_o_motivo()
    {
        var video = Restrito();
        var concessao = AccessGrant.Create(GrantSubjectType.User, Allan.Value, GrantTargetType.Video, video.Id,
            Admin, Agora, startsAt: Agora.AddDays(3));

        var decisao = Avaliar(Convidado, video, [concessao]);

        Assert.False(decisao.Allowed);
        Assert.Equal(AccessReason.GrantNotStarted, decisao.Reason);
    }

    [Fact]
    public void Concessao_revogada_informa_o_motivo()
    {
        var video = Restrito();
        var concessao = AccessGrant.ForUser(Allan, GrantTargetType.Video, video.Id, Admin, Agora);
        concessao.Revoke(Agora);

        var decisao = Avaliar(Convidado, video, [concessao]);

        Assert.False(decisao.Allowed);
        Assert.Equal(AccessReason.GrantRevoked, decisao.Reason);
    }

    [Fact]
    public void Concessao_esgotada_informa_o_motivo()
    {
        var video = Restrito();
        var concessao = AccessGrant.ForLink("resumo", GrantTargetType.Video, video.Id, Admin, Agora, maxViews: 1);
        concessao.RegisterUse(Agora);

        var decisao = Avaliar(Viewer.WithLink(concessao.Id), video, [concessao]);

        Assert.False(decisao.Allowed);
        Assert.Equal(AccessReason.GrantExhausted, decisao.Reason);
    }

    [Fact]
    public void Reproducao_que_consumiu_a_ultima_visualizacao_continua()
    {
        var video = Restrito();
        var concessao = AccessGrant.ForLink("resumo", GrantTargetType.Video, video.Id, Admin, Agora, maxViews: 1);
        concessao.RegisterUse(Agora);

        var espectador = Viewer.WithLink(concessao.Id).ContinuingView(video.Id, concessao.Id);

        Assert.True(Avaliar(espectador, video, [concessao]).Allowed);
    }

    [Fact]
    public void Reproducao_nova_nao_se_apoia_na_anterior()
    {
        var video = Restrito();
        var concessao = AccessGrant.ForLink("resumo", GrantTargetType.Video, video.Id, Admin, Agora, maxViews: 1);
        concessao.RegisterUse(Agora);

        var espectador = Viewer.WithLink(concessao.Id).ContinuingView(video.Id, concessao.Id).StartingNewView();

        Assert.Equal(AccessReason.GrantExhausted, Avaliar(espectador, video, [concessao]).Reason);
    }

    [Fact]
    public void Reproducao_em_andamento_vale_so_para_o_proprio_video()
    {
        var video = Restrito();
        var outro = Restrito();
        var concessao = AccessGrant.ForLink("resumo", GrantTargetType.All, null, Admin, Agora, maxViews: 1);
        concessao.RegisterUse(Agora);

        var espectador = Viewer.WithLink(concessao.Id).ContinuingView(video.Id, concessao.Id);

        Assert.False(Avaliar(espectador, outro, [concessao]).Allowed);
    }

    [Fact]
    public void Reproducao_em_andamento_nao_sobrevive_a_revogacao()
    {
        var video = Restrito();
        var concessao = AccessGrant.ForLink("resumo", GrantTargetType.Video, video.Id, Admin, Agora, maxViews: 1);
        concessao.RegisterUse(Agora);
        concessao.Revoke(Agora);

        var espectador = Viewer.WithLink(concessao.Id).ContinuingView(video.Id, concessao.Id);

        Assert.Equal(AccessReason.GrantRevoked, Avaliar(espectador, video, [concessao]).Reason);
    }

    [Fact]
    public void Uma_concessao_valida_prevalece_sobre_outra_vencida()
    {
        var video = Restrito();
        var vencida = AccessGrant.ForUser(Allan, GrantTargetType.Video, video.Id, Admin, Agora, expiresAt: Agora.AddDays(1));
        var valida = AccessGrant.ForUser(Allan, GrantTargetType.All, null, Admin, Agora);

        var decisao = Avaliar(Convidado, video, [vencida, valida], quando: Agora.AddDays(5));

        Assert.True(decisao.Allowed);
    }

    [Fact]
    public void Revogar_uma_concessao_nao_derruba_o_acesso_vindo_de_outra()
    {
        var video = Restrito();
        var revogada = AccessGrant.ForUser(Allan, GrantTargetType.Video, video.Id, Admin, Agora);
        revogada.Revoke(Agora);
        var porDominio = AccessGrant.ForDomain("barcelos.dev", GrantTargetType.Video, video.Id, Admin, Agora);

        Assert.True(Avaliar(Convidado, video, [revogada, porDominio]).Allowed);
    }

    [Fact]
    public void Sem_nenhuma_concessao_o_motivo_e_a_ausencia_dela()
    {
        var decisao = Avaliar(Convidado, Restrito(), []);

        Assert.False(decisao.Allowed);
        Assert.Equal(AccessReason.NoGrant, decisao.Reason);
    }

    [Fact]
    public void Concessao_nao_alcanca_video_de_outro_alvo()
    {
        var video = Restrito();
        var deOutroVideo = AccessGrant.ForUser(Allan, GrantTargetType.Video, Guid.CreateVersion7(), Admin, Agora);

        var decisao = Avaliar(Convidado, video, [deOutroVideo]);

        Assert.False(decisao.Allowed);
        Assert.Equal(AccessReason.NoGrant, decisao.Reason);
    }

    [Fact]
    public void A_exclusao_do_video_derruba_qualquer_concessao()
    {
        var video = Restrito();
        var concessao = AccessGrant.ForUser(Allan, GrantTargetType.All, null, Admin, Agora);
        video.SoftDelete(Agora);

        var decisao = Avaliar(Convidado, video, [concessao]);

        Assert.False(decisao.Allowed);
        Assert.Equal(AccessReason.VideoDeleted, decisao.Reason);
    }

    [Fact]
    public void Video_em_processamento_nao_e_liberado_nem_com_concessao()
    {
        var video = Video.CreateDraft("Novo", "novo", "originals/a.mp4", Admin, Agora);
        video.MarkUploaded(1024);
        var concessao = AccessGrant.ForUser(Allan, GrantTargetType.All, null, Admin, Agora);

        var decisao = Avaliar(Convidado, video, [concessao]);

        Assert.False(decisao.Allowed);
        Assert.Equal(AccessReason.VideoNotReady, decisao.Reason);
    }

    [Fact]
    public void Video_privado_ignora_concessoes()
    {
        var video = Restrito();
        video.ChangeVisibility(VideoVisibility.Private);
        var concessao = AccessGrant.ForUser(Allan, GrantTargetType.All, null, Admin, Agora);

        var decisao = Avaliar(Convidado, video, [concessao]);

        Assert.False(decisao.Allowed);
        Assert.Equal(AccessReason.PrivateVideo, decisao.Reason);
    }

    [Fact]
    public void Video_publico_dispensa_concessao()
    {
        var video = Restrito();
        video.ChangeVisibility(VideoVisibility.Public);

        var decisao = Avaliar(Viewer.Anonymous, video, []);

        Assert.True(decisao.Allowed);
        Assert.Equal(AccessReason.PublicVideo, decisao.Reason);
    }

    [Fact]
    public void O_administrador_dispensa_concessao_em_qualquer_situacao()
    {
        var administrador = Viewer.Authenticated(Guid.CreateVersion7(), EmailAddress.Parse("admin@opentube.org"), isAdmin: true);
        var video = Restrito();

        Assert.True(Avaliar(administrador, video, []).Allowed);
    }

    [Fact]
    public void O_prazo_relativo_e_respeitado_na_avaliacao()
    {
        var video = Restrito();
        var concessao = AccessGrant.ForUser(Allan, GrantTargetType.Video, video.Id, Admin, Agora,
            durationAfterFirstUse: TimeSpan.FromDays(7));

        Assert.True(Avaliar(Convidado, video, [concessao], quando: Agora.AddYears(1)).Allowed);

        concessao.RegisterUse(Agora.AddYears(1));

        Assert.True(Avaliar(Convidado, video, [concessao], quando: Agora.AddYears(1).AddDays(6)).Allowed);
        Assert.False(Avaliar(Convidado, video, [concessao], quando: Agora.AddYears(1).AddDays(8)).Allowed);
    }

    [Fact]
    public void Exige_todos_os_argumentos()
    {
        var video = Restrito();

        Assert.Throws<ArgumentNullException>(() => AccessPolicy.Evaluate(null!, video, [], [], Agora));
        Assert.Throws<ArgumentNullException>(() => AccessPolicy.Evaluate(Convidado, null!, [], [], Agora));
        Assert.Throws<ArgumentNullException>(() => AccessPolicy.Evaluate(Convidado, video, null!, [], Agora));
        Assert.Throws<ArgumentNullException>(() => AccessPolicy.Evaluate(Convidado, video, [], null!, Agora));
    }
}
