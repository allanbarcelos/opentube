// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Access;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;

namespace OpenTube.Domain.Tests.Access;

public class AccessPolicyTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static readonly Viewer Convidado =
        Viewer.Authenticated(Guid.CreateVersion7(), EmailAddress.Parse("allan@barcelos.dev"));

    private static readonly Viewer Administrador =
        Viewer.Authenticated(Guid.CreateVersion7(), EmailAddress.Parse("admin@opentube.org"), isAdmin: true);

    private static Video VideoPronto(VideoVisibility visibilidade)
    {
        var video = Video.CreateDraft("Vídeo", "video", "originals/a.mp4", Guid.CreateVersion7(), Agora);
        video.MarkUploaded(1024);
        video.StartProcessing();
        video.MarkReady("vod/a/", 120, 1280, 720, null, null, Agora);
        if (visibilidade is not VideoVisibility.Private)
            video.ChangeVisibility(visibilidade);
        return video;
    }

    [Fact]
    public void Administrador_enxerga_todo_o_acervo()
    {
        foreach (var visibilidade in Enum.GetValues<VideoVisibility>())
        {
            var decisao = AccessPolicy.Evaluate(Administrador, VideoPronto(visibilidade));

            Assert.True(decisao.Allowed);
            Assert.Equal(AccessReason.Administrator, decisao.Reason);
        }
    }

    [Fact]
    public void Administrador_enxerga_video_ainda_em_processamento()
    {
        var video = Video.CreateDraft("Vídeo", "video", "originals/a.mp4", Guid.CreateVersion7(), Agora);

        Assert.True(AccessPolicy.CanWatch(Administrador, video));
    }

    [Fact]
    public void Administrador_enxerga_video_excluido_para_poder_restaurar()
    {
        var video = VideoPronto(VideoVisibility.Public);
        video.SoftDelete(Agora);

        Assert.True(AccessPolicy.CanWatch(Administrador, video));
    }

    [Fact]
    public void Visitante_anonimo_assiste_ao_video_publico()
    {
        var decisao = AccessPolicy.Evaluate(Viewer.Anonymous, VideoPronto(VideoVisibility.Public));

        Assert.True(decisao.Allowed);
        Assert.Equal(AccessReason.PublicVideo, decisao.Reason);
    }

    [Fact]
    public void Visitante_anonimo_nao_assiste_ao_video_privado()
    {
        var decisao = AccessPolicy.Evaluate(Viewer.Anonymous, VideoPronto(VideoVisibility.Private));

        Assert.False(decisao.Allowed);
        Assert.Equal(AccessReason.PrivateVideo, decisao.Reason);
    }

    [Fact]
    public void Convidado_autenticado_nao_assiste_ao_video_privado()
    {
        var decisao = AccessPolicy.Evaluate(Convidado, VideoPronto(VideoVisibility.Private));

        Assert.False(decisao.Allowed);
        Assert.Equal(AccessReason.PrivateVideo, decisao.Reason);
    }

    [Fact]
    public void Video_restrito_sem_concessao_e_negado()
    {
        var decisao = AccessPolicy.Evaluate(Convidado, VideoPronto(VideoVisibility.Restricted));

        Assert.False(decisao.Allowed);
        Assert.Equal(AccessReason.NoGrant, decisao.Reason);
    }

    [Fact]
    public void Video_publico_em_processamento_e_negado_a_quem_nao_e_administrador()
    {
        var video = Video.CreateDraft("Vídeo", "video", "originals/a.mp4", Guid.CreateVersion7(), Agora);
        video.MarkUploaded(1024);
        video.StartProcessing();

        var decisao = AccessPolicy.Evaluate(Convidado, video);

        Assert.False(decisao.Allowed);
        Assert.Equal(AccessReason.VideoNotReady, decisao.Reason);
    }

    [Fact]
    public void Exclusao_corta_o_acesso_de_imediato()
    {
        var video = VideoPronto(VideoVisibility.Public);
        video.SoftDelete(Agora);

        var decisao = AccessPolicy.Evaluate(Viewer.Anonymous, video);

        Assert.False(decisao.Allowed);
        Assert.Equal(AccessReason.VideoDeleted, decisao.Reason);
    }

    [Fact]
    public void Exclusao_tem_precedencia_sobre_o_estado_de_processamento()
    {
        var video = Video.CreateDraft("Vídeo", "video", "originals/a.mp4", Guid.CreateVersion7(), Agora);
        video.SoftDelete(Agora);

        Assert.Equal(AccessReason.VideoDeleted, AccessPolicy.Evaluate(Convidado, video).Reason);
    }

    [Fact]
    public void Exige_espectador_e_video()
    {
        Assert.Throws<ArgumentNullException>(() => AccessPolicy.Evaluate(null!, VideoPronto(VideoVisibility.Public)));
        Assert.Throws<ArgumentNullException>(() => AccessPolicy.Evaluate(Viewer.Anonymous, null!));
    }
}
