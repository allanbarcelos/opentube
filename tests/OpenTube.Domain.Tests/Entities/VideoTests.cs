// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;

namespace OpenTube.Domain.Tests.Entities;

public class VideoTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();

    private static Video Rascunho() =>
        Video.CreateDraft("Reunião Trimestral", "reuniao-trimestral", "originals/abc.mp4", Admin, Agora);

    private static Video Pronto()
    {
        var video = Rascunho();
        video.MarkUploaded(1_048_576);
        video.StartProcessing();
        video.MarkReady("vod/abc/", 600, 1920, 1080, "vod/abc/thumb.jpg", "vod/abc/sprite.webp", Agora);
        return video;
    }

    [Fact]
    public void Nasce_privado_e_em_rascunho()
    {
        var video = Rascunho();

        Assert.Equal(VideoVisibility.Private, video.Visibility);
        Assert.Equal(VideoStatus.Draft, video.Status);
        Assert.False(video.IsPlayable);
        Assert.NotEqual(Guid.Empty, video.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Exige_titulo(string? titulo)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            Video.CreateDraft(titulo!, "slug", "originals/a.mp4", Admin, Agora));
    }

    [Fact]
    public void Exige_slug_e_chave_do_arquivo()
    {
        Assert.ThrowsAny<ArgumentException>(() => Video.CreateDraft("Título", "", "originals/a.mp4", Admin, Agora));
        Assert.ThrowsAny<ArgumentException>(() => Video.CreateDraft("Título", "slug", "", Admin, Agora));
    }

    [Fact]
    public void Limpa_espacos_do_titulo_e_da_descricao()
    {
        var video = Video.CreateDraft("  Título  ", "slug", "originals/a.mp4", Admin, Agora, "  texto  ");

        Assert.Equal("Título", video.Title);
        Assert.Equal("texto", video.Description);
    }

    [Fact]
    public void Descricao_em_branco_vira_nula()
    {
        var video = Video.CreateDraft("Título", "slug", "originals/a.mp4", Admin, Agora, "   ");

        Assert.Null(video.Description);
    }

    [Fact]
    public void Registra_o_envio_do_arquivo()
    {
        var video = Rascunho();

        video.MarkUploaded(2048);

        Assert.Equal(VideoStatus.Uploaded, video.Status);
        Assert.Equal(2048, video.SizeBytes);
    }

    [Fact]
    public void Recusa_arquivo_vazio()
    {
        var video = Rascunho();

        Assert.Throws<ArgumentOutOfRangeException>(() => video.MarkUploaded(0));
    }

    [Fact]
    public void Recusa_envio_duplicado()
    {
        var video = Rascunho();
        video.MarkUploaded(2048);

        Assert.Throws<InvalidOperationException>(() => video.MarkUploaded(2048));
    }

    [Fact]
    public void Percorre_o_pipeline_ate_ficar_pronto()
    {
        var video = Pronto();

        Assert.Equal(VideoStatus.Ready, video.Status);
        Assert.True(video.IsPlayable);
        Assert.Equal("vod/abc/", video.HlsPrefix);
        Assert.Equal(600, video.DurationSeconds);
        Assert.Equal(1920, video.Width);
        Assert.Equal(Agora, video.PublishedAt);
    }

    [Fact]
    public void Nao_processa_rascunho_sem_arquivo()
    {
        var video = Rascunho();

        Assert.Throws<InvalidOperationException>(video.StartProcessing);
    }

    [Fact]
    public void Reprocessa_video_que_falhou()
    {
        var video = Rascunho();
        video.MarkUploaded(1024);
        video.StartProcessing();
        video.MarkFailed();

        video.StartProcessing();

        Assert.Equal(VideoStatus.Processing, video.Status);
    }

    [Fact]
    public void Retoma_processamento_interrompido()
    {
        var video = Rascunho();
        video.MarkUploaded(1024);
        video.StartProcessing();

        // O worker morreu no meio: o trabalho volta para a fila e encontra o vídeo assim.
        video.StartProcessing();

        Assert.Equal(VideoStatus.Processing, video.Status);
    }

    [Fact]
    public void Reprocessar_mantem_o_video_pronto_no_ar()
    {
        var video = Pronto();

        video.StartProcessing();

        Assert.Equal(VideoStatus.Ready, video.Status);
        Assert.True(video.IsPlayable);
        Assert.Equal("vod/abc/", video.HlsPrefix);
    }

    [Fact]
    public void Reprocessar_troca_a_versao_em_uso()
    {
        var video = Pronto();

        video.StartProcessing();
        video.MarkReady("vod/abc-v2/", 620, 1920, 1080, "vod/abc-v2/thumb.jpg", null, Agora.AddDays(1));

        Assert.Equal(VideoStatus.Ready, video.Status);
        Assert.Equal("vod/abc-v2/", video.HlsPrefix);
        Assert.Equal("vod/abc-v2/thumb.jpg", video.ThumbnailKey);
    }

    [Fact]
    public void Nao_fica_pronto_sem_passar_por_processamento()
    {
        var video = Rascunho();
        video.MarkUploaded(1024);

        Assert.Throws<InvalidOperationException>(() =>
            video.MarkReady("vod/abc/", 10, 1280, 720, null, null, Agora));
    }

    [Fact]
    public void Exige_duracao_positiva_ao_concluir()
    {
        var video = Rascunho();
        video.MarkUploaded(1024);
        video.StartProcessing();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            video.MarkReady("vod/abc/", 0, 1280, 720, null, null, Agora));
    }

    [Fact]
    public void Video_pronto_nao_volta_para_falha()
    {
        var video = Pronto();

        Assert.Throws<InvalidOperationException>(video.MarkFailed);
    }

    [Fact]
    public void Preserva_a_primeira_data_de_publicacao_em_reprocessamentos()
    {
        var video = Pronto();
        var depois = Agora.AddDays(30);

        video.StartProcessing();
        video.MarkReady("vod/abc-v2/", 620, 1920, 1080, null, null, depois);

        Assert.Equal(Agora, video.PublishedAt);
    }

    [Fact]
    public void Torna_publico_um_video_pronto()
    {
        var video = Pronto();

        video.ChangeVisibility(VideoVisibility.Public);

        Assert.Equal(VideoVisibility.Public, video.Visibility);
    }

    [Theory]
    [InlineData(VideoVisibility.Public)]
    [InlineData(VideoVisibility.Restricted)]
    public void Nao_libera_video_que_ainda_nao_esta_pronto(VideoVisibility visibilidade)
    {
        var video = Rascunho();

        Assert.Throws<InvalidOperationException>(() => video.ChangeVisibility(visibilidade));
    }

    [Fact]
    public void Sempre_pode_voltar_a_ser_privado()
    {
        var video = Rascunho();

        video.ChangeVisibility(VideoVisibility.Private);

        Assert.Equal(VideoVisibility.Private, video.Visibility);
    }

    [Fact]
    public void Exclusao_logica_preserva_o_registro_e_fecha_o_acesso()
    {
        var video = Pronto();
        video.ChangeVisibility(VideoVisibility.Public);

        video.SoftDelete(Agora);

        Assert.True(video.IsDeleted);
        Assert.False(video.IsPlayable);
        Assert.Equal(VideoVisibility.Private, video.Visibility);
        Assert.Equal(Agora, video.DeletedAt);
    }

    [Fact]
    public void Excluir_duas_vezes_mantem_a_data_original()
    {
        var video = Pronto();
        video.SoftDelete(Agora);

        video.SoftDelete(Agora.AddDays(1));

        Assert.Equal(Agora, video.DeletedAt);
    }

    [Fact]
    public void Restaura_video_excluido()
    {
        var video = Pronto();
        video.SoftDelete(Agora);

        video.Restore();

        Assert.False(video.IsDeleted);
        Assert.True(video.IsPlayable);
    }

    [Fact]
    public void Normaliza_etiquetas_removendo_repeticao_e_caixa()
    {
        var video = Rascunho();

        video.ReplaceTags(["Treinamento", "  segurança ", "TREINAMENTO", "", "   "]);

        Assert.Equal(["treinamento", "segurança"], video.Tags);
    }

    [Fact]
    public void Limita_a_quantidade_de_etiquetas()
    {
        var video = Rascunho();

        video.ReplaceTags(Enumerable.Range(1, 50).Select(i => $"tag{i}"));

        Assert.Equal(30, video.Tags.Count);
    }

    [Fact]
    public void Substitui_as_etiquetas_anteriores()
    {
        var video = Rascunho();
        video.ReplaceTags(["antiga"]);

        video.ReplaceTags(["nova"]);

        Assert.Equal(["nova"], video.Tags);
    }

    [Fact]
    public void Transcricao_em_branco_vira_nula()
    {
        var video = Rascunho();

        video.SetTranscript("   ");

        Assert.Null(video.Transcript);
    }

    [Fact]
    public void Guarda_a_transcricao_sem_espacos_nas_pontas()
    {
        var video = Rascunho();

        video.SetTranscript("  bom dia a todos  ");

        Assert.Equal("bom dia a todos", video.Transcript);
    }

    [Fact]
    public void Acumula_arquivos_derivados()
    {
        var video = Rascunho();

        video.AddAsset(VideoAsset.Create(video.Id, VideoAssetKind.Caption, "vod/abc/pt.vtt", Agora, "pt-BR"));

        Assert.Single(video.Assets);
        Assert.Equal(VideoAssetKind.Caption, video.Assets.First().Kind);
    }
}
