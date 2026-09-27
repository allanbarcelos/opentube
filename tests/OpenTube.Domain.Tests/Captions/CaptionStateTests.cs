using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;

namespace OpenTube.Domain.Tests.Captions;

public class CaptionStateTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Video = Guid.CreateVersion7();

    [Fact]
    public void Pedido_de_transcricao_nasce_processando_e_sem_conteudo()
    {
        var legenda = VideoAsset.CaptionTranscriptionRequest(Video, "PT-BR", null, "v/captions/pt-br.vtt", Agora);

        Assert.Equal(CaptionStatus.Processing, legenda.Status);
        Assert.Equal(CaptionSource.Automatic, legenda.Source);
        Assert.Equal("pt-br", legenda.Language);
        Assert.False(legenda.HasContent);
    }

    [Fact]
    public void Nao_aceita_segundo_pedido_do_mesmo_idioma_em_andamento()
    {
        var legenda = VideoAsset.CaptionTranscriptionRequest(Video, "pt-br", null, "chave", Agora);

        var erro = Assert.Throws<InvalidOperationException>(() => legenda.StartTranscription(Agora));
        Assert.Equal("A transcription for this language is already in progress.", erro.Message);
    }

    [Fact]
    public void Transcricao_concluida_deixa_a_legenda_pronta()
    {
        var legenda = VideoAsset.CaptionTranscriptionRequest(Video, "pt-br", null, "chave", Agora);

        legenda.CompleteTranscription(1234, Agora.AddMinutes(5));

        Assert.Equal(CaptionStatus.Ready, legenda.Status);
        Assert.True(legenda.HasContent);
        Assert.Equal(1234, legenda.SizeBytes);
        Assert.Equal(Agora.AddMinutes(5), legenda.ContentUpdatedAt);
    }

    [Fact]
    public void Falha_guarda_o_motivo_e_mantem_o_conteudo_anterior()
    {
        var legenda = VideoAsset.CaptionWithContent(Video, "en", "English", "chave", CaptionSource.Upload, 10, Agora);
        legenda.StartTranscription(Agora.AddMinutes(1));

        legenda.FailTranscription("modelo não encontrado", Agora.AddMinutes(2));

        Assert.Equal(CaptionStatus.Failed, legenda.Status);
        Assert.Equal("modelo não encontrado", legenda.Error);
        Assert.True(legenda.HasContent);
        Assert.Equal(Agora, legenda.ContentUpdatedAt);
    }

    [Fact]
    public void Depois_da_falha_pode_pedir_de_novo()
    {
        var legenda = VideoAsset.CaptionTranscriptionRequest(Video, "pt-br", null, "chave", Agora);
        legenda.FailTranscription("erro", Agora);

        legenda.StartTranscription(Agora.AddMinutes(1));

        Assert.Equal(CaptionStatus.Processing, legenda.Status);
        Assert.Null(legenda.Error);
    }

    [Fact]
    public void Envio_ou_edicao_sao_recusados_durante_a_transcricao()
    {
        var legenda = VideoAsset.CaptionTranscriptionRequest(Video, "pt-br", null, "chave", Agora);

        var erro = Assert.Throws<InvalidOperationException>(() => legenda.ReplaceContent(CaptionSource.Edited, 10, Agora));
        Assert.Equal("Wait for the transcription of this language to finish.", erro.Message);
    }

    [Fact]
    public void Edicao_marca_a_origem_e_limpa_a_falha()
    {
        var legenda = VideoAsset.CaptionTranscriptionRequest(Video, "pt-br", null, "chave", Agora);
        legenda.FailTranscription("erro", Agora);

        legenda.ReplaceContent(CaptionSource.Edited, 50, Agora.AddMinutes(3));

        Assert.Equal(CaptionStatus.Ready, legenda.Status);
        Assert.Equal(CaptionSource.Edited, legenda.Source);
        Assert.Null(legenda.Error);
        Assert.True(legenda.HasContent);
    }

    [Fact]
    public void Motivo_da_falha_e_limitado()
    {
        var legenda = VideoAsset.CaptionTranscriptionRequest(Video, "pt-br", null, "chave", Agora);

        legenda.FailTranscription(new string('x', 2000), Agora);

        Assert.Equal(500, legenda.Error!.Length);
    }

    [Fact]
    public void Estado_de_processamento_e_so_de_legenda()
    {
        var miniatura = VideoAsset.Create(Video, VideoAssetKind.Thumbnail, "thumb.jpg", Agora);

        Assert.Throws<InvalidOperationException>(() => miniatura.StartTranscription(Agora));
    }
}
