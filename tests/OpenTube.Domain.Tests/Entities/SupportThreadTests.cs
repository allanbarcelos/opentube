using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;

namespace OpenTube.Domain.Tests.Entities;

public class SupportThreadTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Video = Guid.CreateVersion7();
    private static readonly Guid Autor = Guid.CreateVersion7();
    private static readonly Guid Admin = Guid.CreateVersion7();

    private static SupportThread Nova(double? instante = null) =>
        SupportThread.Open(Video, Autor, "Não consigo ouvir o áudio", Agora, instante);

    [Fact]
    public void Nasce_aberta_com_a_primeira_mensagem()
    {
        var conversa = Nova();

        Assert.Equal(SupportStatus.Open, conversa.Status);
        Assert.Single(conversa.Messages);
        Assert.False(conversa.Messages[0].FromAdmin);
        Assert.Equal("Não consigo ouvir o áudio", conversa.Messages[0].Body);
        Assert.Equal(Agora, conversa.LastMessageAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Exige_a_primeira_mensagem(string? texto)
    {
        Assert.ThrowsAny<ArgumentException>(() => SupportThread.Open(Video, Autor, texto!, Agora));
    }

    [Fact]
    public void Guarda_o_instante_do_video_quando_informado()
    {
        Assert.Equal(83, Nova(83).TimestampSeconds);
        Assert.Null(Nova(0).TimestampSeconds);
        Assert.Null(Nova(-5).TimestampSeconds);
    }

    [Fact]
    public void A_resposta_da_administracao_devolve_a_bola_para_o_autor()
    {
        var conversa = Nova();

        conversa.Reply(Admin, "O áudio está no segundo canal, verifique o fone", fromAdmin: true, Agora.AddMinutes(10));

        Assert.Equal(SupportStatus.Answered, conversa.Status);
        Assert.Equal(2, conversa.Messages.Count);
        Assert.True(conversa.Messages[1].FromAdmin);
        Assert.True(conversa.HasUnreadReply);
        Assert.False(conversa.NeedsAdminAttention);
    }

    [Fact]
    public void A_replica_do_autor_devolve_a_conversa_para_a_administracao()
    {
        var conversa = Nova();
        conversa.Reply(Admin, "Resposta", fromAdmin: true, Agora.AddMinutes(10));

        conversa.Reply(Autor, "Continua sem áudio", fromAdmin: false, Agora.AddMinutes(20));

        Assert.Equal(SupportStatus.Open, conversa.Status);
        Assert.True(conversa.NeedsAdminAttention);
        Assert.False(conversa.HasUnreadReply);
    }

    [Fact]
    public void Quem_escreve_ja_conta_como_tendo_lido()
    {
        // Sem isso, a própria mensagem apareceria como pendente para quem a escreveu.
        var conversa = Nova();

        conversa.Reply(Admin, "Resposta", fromAdmin: true, Agora.AddMinutes(10));

        Assert.Equal(Agora.AddMinutes(10), conversa.ReadByAdminAt);
    }

    [Fact]
    public void Outra_pessoa_nao_responde_pelo_autor()
    {
        var conversa = Nova();

        Assert.Throws<InvalidOperationException>(() =>
            conversa.Reply(Guid.CreateVersion7(), "Intrusão", fromAdmin: false, Agora));
    }

    [Fact]
    public void Conversa_encerrada_nao_aceita_mensagem()
    {
        var conversa = Nova();
        conversa.Close(Agora.AddHours(1));

        Assert.Throws<InvalidOperationException>(() => conversa.Reply(Autor, "Mais uma", fromAdmin: false, Agora.AddHours(2)));
        Assert.Throws<InvalidOperationException>(() => conversa.Reply(Admin, "Mais uma", fromAdmin: true, Agora.AddHours(2)));
    }

    [Fact]
    public void Reabrir_devolve_a_conversa_para_a_fila()
    {
        var conversa = Nova();
        conversa.Close(Agora.AddHours(1));

        conversa.Reopen(Agora.AddHours(2));

        Assert.Equal(SupportStatus.Open, conversa.Status);
        Assert.Null(conversa.ClosedAt);
        Assert.False(conversa.IsClosed);
    }

    [Fact]
    public void Marcar_como_lida_limpa_a_pendencia()
    {
        var conversa = Nova();
        conversa.Reply(Admin, "Resposta", fromAdmin: true, Agora.AddMinutes(10));

        Assert.True(conversa.HasUnreadReply);

        conversa.MarkReadByUser(Agora.AddMinutes(11));

        Assert.False(conversa.HasUnreadReply);
    }

    [Fact]
    public void A_administracao_marca_a_propria_leitura()
    {
        var conversa = Nova();

        Assert.True(conversa.NeedsAdminAttention);

        conversa.MarkReadByAdmin(Agora.AddMinutes(1));

        Assert.False(conversa.NeedsAdminAttention);
    }

    [Fact]
    public void Conversa_encerrada_nao_fica_pendente_de_ninguem()
    {
        var conversa = Nova();
        conversa.Close(Agora.AddHours(1));

        Assert.False(conversa.NeedsAdminAttention);
        Assert.False(conversa.HasUnreadReply);
    }

    [Fact]
    public void So_o_autor_e_a_administracao_enxergam_a_conversa()
    {
        var conversa = Nova();

        Assert.True(conversa.IsVisibleTo(Autor, isAdmin: false));
        Assert.True(conversa.IsVisibleTo(Admin, isAdmin: true));
        Assert.True(conversa.IsVisibleTo(null, isAdmin: true));
        Assert.False(conversa.IsVisibleTo(Guid.CreateVersion7(), isAdmin: false));
        Assert.False(conversa.IsVisibleTo(null, isAdmin: false));
    }

    [Fact]
    public void A_mensagem_e_aparada_e_limitada_no_tamanho()
    {
        var conversa = SupportThread.Open(Video, Autor, "  com espaços  ", Agora);

        Assert.Equal("com espaços", conversa.Messages[0].Body);

        var longa = conversa.Reply(Autor, new string('x', SupportMessage.MaxLength + 500), fromAdmin: false, Agora);

        Assert.Equal(SupportMessage.MaxLength, longa.Body.Length);
    }

    [Fact]
    public void Exige_texto_na_resposta()
    {
        var conversa = Nova();

        Assert.ThrowsAny<ArgumentException>(() => conversa.Reply(Autor, "   ", fromAdmin: false, Agora));
    }
}
