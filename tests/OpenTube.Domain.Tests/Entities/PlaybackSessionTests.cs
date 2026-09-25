using OpenTube.Domain.Analytics;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;

namespace OpenTube.Domain.Tests.Entities;

public class PlaybackSessionTests
{
    private const double Duracao = 600;

    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Video = Guid.CreateVersion7();
    private static readonly Guid Usuario = Guid.CreateVersion7();

    private static PlaybackSession Nova(Guid? usuario = null, string? anonimo = null) =>
        PlaybackSession.Start(Video, Agora, usuario ?? Usuario, anonimo,
            client: new ClientProfile(DeviceType.Desktop, "macOS", "Safari"));

    [Fact]
    public void Nasce_sem_nada_assistido()
    {
        var sessao = Nova();

        Assert.Equal(0, sessao.WatchedSeconds);
        Assert.Empty(sessao.Intervals);
        Assert.False(sessao.Completed);
        Assert.Equal(Agora, sessao.StartedAt);
        Assert.Equal(DeviceType.Desktop, sessao.Device);
    }

    [Fact]
    public void Visitante_anonimo_e_identificado_pelo_navegador()
    {
        var sessao = PlaybackSession.Start(Video, Agora, anonymousId: "  visitante-123  ");

        Assert.Null(sessao.UserId);
        Assert.Equal("visitante-123", sessao.AnonymousId);
    }

    [Fact]
    public void Pessoa_autenticada_dispensa_o_identificador_anonimo()
    {
        var sessao = PlaybackSession.Start(Video, Agora, Usuario, "visitante-123");

        Assert.Equal(Usuario, sessao.UserId);
        Assert.Null(sessao.AnonymousId);
    }

    [Fact]
    public void Exige_pessoa_ou_identificador_de_visitante()
    {
        Assert.Throws<ArgumentException>(() => PlaybackSession.Start(Video, Agora));
    }

    [Fact]
    public void Acumula_os_trechos_assistidos()
    {
        var sessao = Nova();

        sessao.Record(new WatchInterval(0, 30), Agora, Duracao);
        sessao.Record(new WatchInterval(30, 60), Agora.AddSeconds(30), Duracao);

        Assert.Equal(60, sessao.WatchedSeconds);
        Assert.Single(sessao.Intervals);
    }

    [Fact]
    public void Reassistir_nao_infla_o_tempo()
    {
        var sessao = Nova();

        sessao.Record(new WatchInterval(0, 60), Agora, Duracao);
        sessao.Record(new WatchInterval(0, 60), Agora.AddSeconds(60), Duracao);

        Assert.Equal(60, sessao.WatchedSeconds);
    }

    [Fact]
    public void Trechos_separados_sao_preservados()
    {
        var sessao = Nova();

        sessao.Record(new WatchInterval(0, 60), Agora, Duracao);
        sessao.Record(new WatchInterval(300, 360), Agora, Duracao);

        Assert.Equal(2, sessao.Intervals.Count);
        Assert.Equal(120, sessao.WatchedSeconds);
    }

    [Fact]
    public void Trecho_vazio_apenas_atualiza_o_ultimo_sinal()
    {
        var sessao = Nova();

        sessao.Record(new WatchInterval(10, 10), Agora.AddMinutes(1), Duracao);

        Assert.Empty(sessao.Intervals);
        Assert.Equal(Agora.AddMinutes(1), sessao.LastSeenAt);
    }

    [Fact]
    public void Chegar_ao_fim_marca_a_sessao_como_concluida()
    {
        var sessao = Nova();

        sessao.Record(new WatchInterval(0, Duracao), Agora, Duracao);

        Assert.True(sessao.Completed);
    }

    [Fact]
    public void Faltando_poucos_segundos_ainda_conta_como_concluida()
    {
        // Os últimos segundos costumam ficar de fora por causa dos créditos.
        var sessao = Nova();

        sessao.Record(new WatchInterval(0, Duracao - 3), Agora, Duracao);

        Assert.True(sessao.Completed);
    }

    [Fact]
    public void Parar_antes_do_fim_nao_conta_como_concluida()
    {
        var sessao = Nova();

        sessao.Record(new WatchInterval(0, 500), Agora, Duracao);

        Assert.False(sessao.Completed);
    }

    [Fact]
    public void Saltar_avanca_o_ponto_alcancado_sem_contar_como_assistido()
    {
        var sessao = Nova();
        sessao.Record(new WatchInterval(0, 30), Agora, Duracao);

        sessao.Seek(500, Agora.AddSeconds(31));

        Assert.Equal(500, sessao.FurthestPosition);
        Assert.Equal(30, sessao.WatchedSeconds);
    }

    [Fact]
    public void Saltar_para_tras_nao_reduz_o_ponto_alcancado()
    {
        var sessao = Nova();
        sessao.Seek(300, Agora);

        sessao.Seek(10, Agora.AddSeconds(1));

        Assert.Equal(300, sessao.FurthestPosition);
    }

    [Fact]
    public void Guarda_a_maior_qualidade_usada()
    {
        var sessao = Nova();

        sessao.RecordQuality("480p", Agora);
        sessao.RecordQuality("1080p", Agora);
        sessao.RecordQuality("720p", Agora);

        Assert.Equal("1080p", sessao.MaxQuality);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Qualidade_em_branco_e_ignorada(string? qualidade)
    {
        var sessao = Nova();
        sessao.RecordQuality("720p", Agora);

        sessao.RecordQuality(qualidade, Agora);

        Assert.Equal("720p", sessao.MaxQuality);
    }

    [Fact]
    public void Conta_os_erros_relatados()
    {
        var sessao = Nova();

        sessao.RecordError(Agora);
        sessao.RecordError(Agora.AddSeconds(5));

        Assert.Equal(2, sessao.ErrorCount);
    }

    [Fact]
    public void Encerrar_duas_vezes_mantem_o_primeiro_encerramento()
    {
        var sessao = Nova();

        sessao.End(Agora.AddMinutes(5));
        sessao.End(Agora.AddMinutes(9));

        Assert.Equal(Agora.AddMinutes(5), sessao.EndedAt);
        Assert.Equal(Agora.AddMinutes(9), sessao.LastSeenAt);
    }

    [Fact]
    public void Converte_os_trechos_para_o_formato_dos_calculos()
    {
        var sessao = Nova();
        sessao.Record(new WatchInterval(0, 60), Agora, Duracao);

        var trechos = sessao.ToWatchIntervals();

        Assert.Equal([new WatchInterval(0, 60)], trechos);
    }

    [Fact]
    public void O_endereco_de_origem_e_guardado_como_resumo()
    {
        var sessao = PlaybackSession.Start(Video, Agora, Usuario, ipHash: "resumo-do-ip");

        Assert.Equal("resumo-do-ip", sessao.IpHash);
    }

    [Fact]
    public void Origem_absurdamente_longa_e_truncada()
    {
        var sessao = PlaybackSession.Start(Video, Agora, Usuario, referrer: new string('x', 900));

        Assert.Equal(500, sessao.Referrer!.Length);
    }
}

public class VideoDailyStatTests
{
    [Fact]
    public void Calcula_o_tempo_medio_por_sessao()
    {
        var estatistica = VideoDailyStat.Create(Guid.CreateVersion7(), new DateOnly(2026, 9, 24), 4, 3, 1200, 2);

        Assert.Equal(300, estatistica.AverageWatchSeconds);
    }

    [Fact]
    public void Sem_visualizacoes_a_media_e_zero()
    {
        var estatistica = VideoDailyStat.Create(Guid.CreateVersion7(), new DateOnly(2026, 9, 24), 0, 0, 0, 0);

        Assert.Equal(0, estatistica.AverageWatchSeconds);
    }

    [Fact]
    public void Atualizar_substitui_os_numeros_do_dia()
    {
        var estatistica = VideoDailyStat.Create(Guid.CreateVersion7(), new DateOnly(2026, 9, 24), 1, 1, 60, 0);

        estatistica.Update(5, 4, 900, 3);

        Assert.Equal(5, estatistica.Views);
        Assert.Equal(4, estatistica.UniqueViewers);
        Assert.Equal(900, estatistica.WatchSeconds);
        Assert.Equal(3, estatistica.Completions);
    }
}
