using OpenTube.Domain.Media;
using OpenTube.Worker.Media;

namespace OpenTube.Worker.Tests.Media;

public class FfmpegArgumentsTests
{
    private static readonly IReadOnlyList<Rendition> Ladder = RenditionLadder.For(1920, 1080);

    private static string Linha(IReadOnlyList<string> argumentos) => string.Join(' ', argumentos);

    private static string ValorDe(IReadOnlyList<string> argumentos, string opcao)
    {
        var indice = argumentos.ToList().IndexOf(opcao);
        Assert.True(indice >= 0 && indice + 1 < argumentos.Count, $"opção {opcao} não encontrada");

        return argumentos[indice + 1];
    }

    [Fact]
    public void A_inspecao_pede_json_com_formato_e_fluxos()
    {
        var argumentos = FfmpegArguments.Probe("/tmp/a.mp4");

        Assert.Contains("-show_streams", argumentos);
        Assert.Contains("-show_format", argumentos);
        Assert.Equal("json", ValorDe(argumentos, "-print_format"));
        Assert.Equal("/tmp/a.mp4", argumentos[^1]);
    }

    [Fact]
    public void A_transcodificacao_divide_o_fluxo_uma_vez_para_todas_as_versoes()
    {
        var filtro = ValorDe(FfmpegArguments.Transcode("/tmp/a.mp4", "/tmp/saida", Ladder, 30, true), "-filter_complex");

        Assert.StartsWith("[0:v]split=4", filtro);
        Assert.Contains("[v0]scale=w=640:h=360", filtro);
        Assert.Contains("[v3]scale=w=1920:h=1080", filtro);
        Assert.Contains("setsar=1[v3out]", filtro);
    }

    [Fact]
    public void Cada_versao_ganha_sua_propria_taxa_de_bits()
    {
        var argumentos = FfmpegArguments.Transcode("/tmp/a.mp4", "/tmp/saida", Ladder, 30, true);

        Assert.Equal("800k", ValorDe(argumentos, "-b:v:0"));
        Assert.Equal("5000k", ValorDe(argumentos, "-b:v:3"));
        Assert.Equal("856k", ValorDe(argumentos, "-maxrate:v:0"));
        Assert.Equal("1600k", ValorDe(argumentos, "-bufsize:v:0"));
    }

    [Fact]
    public void Os_quadros_chave_cobrem_exatamente_o_segmento_em_todas_as_versoes()
    {
        var argumentos = FfmpegArguments.Transcode("/tmp/a.mp4", "/tmp/saida", Ladder, 30, true);

        Assert.Equal("120", ValorDe(argumentos, "-g"));
        Assert.Equal("120", ValorDe(argumentos, "-keyint_min"));
        // Sem isso o codificador inseriria quadros-chave em mudanças de cena e desalinharia
        // os segmentos entre as versões.
        Assert.Equal("0", ValorDe(argumentos, "-sc_threshold"));
    }

    [Fact]
    public void O_arquivo_de_inicializacao_leva_o_nome_da_versao()
    {
        var argumentos = FfmpegArguments.Transcode("/tmp/a.mp4", "/tmp/saida", Ladder, 30, true);

        // Sem o marcador da versão o FFmpeg numera sozinho (init_0.mp4) e o nome deixa de
        // ser dedutível a partir da versão.
        Assert.Equal("init-%v.mp4", ValorDe(argumentos, "-hls_fmp4_init_filename"));
        Assert.Equal("init-720p.mp4", FfmpegArguments.InitFileNameFor("720p"));
    }

    [Fact]
    public void Gera_hls_em_fmp4_com_segmentos_independentes()
    {
        var argumentos = FfmpegArguments.Transcode("/tmp/a.mp4", "/tmp/saida", Ladder, 30, true);

        Assert.Equal("hls", ValorDe(argumentos, "-f"));
        Assert.Equal("vod", ValorDe(argumentos, "-hls_playlist_type"));
        Assert.Equal("fmp4", ValorDe(argumentos, "-hls_segment_type"));
        Assert.Equal("4", ValorDe(argumentos, "-hls_time"));
        Assert.Equal("independent_segments", ValorDe(argumentos, "-hls_flags"));
        Assert.Equal(FfmpegArguments.MasterFileName, ValorDe(argumentos, "-master_pl_name"));
    }

    [Fact]
    public void As_pastas_de_saida_levam_o_nome_da_versao()
    {
        var mapa = ValorDe(FfmpegArguments.Transcode("/tmp/a.mp4", "/tmp/saida", Ladder, 30, true), "-var_stream_map");

        Assert.Equal("v:0,a:0,name:360p v:1,a:1,name:480p v:2,a:2,name:720p v:3,a:3,name:1080p", mapa);
    }

    [Fact]
    public void Video_sem_audio_nao_mapeia_trilha_sonora()
    {
        var argumentos = FfmpegArguments.Transcode("/tmp/a.mp4", "/tmp/saida", Ladder, 30, hasAudio: false);

        Assert.DoesNotContain("a:0", argumentos);
        Assert.Equal("v:0,name:360p v:1,name:480p v:2,name:720p v:3,name:1080p", ValorDe(argumentos, "-var_stream_map"));
        Assert.DoesNotContain("-c:a:0", argumentos);
    }

    [Fact]
    public void O_audio_e_normalizado_para_dois_canais()
    {
        var argumentos = FfmpegArguments.Transcode("/tmp/a.mp4", "/tmp/saida", Ladder, 30, true);

        Assert.Equal("aac", ValorDe(argumentos, "-c:a:0"));
        Assert.Equal("2", ValorDe(argumentos, "-ac:a:0"));
        Assert.Equal("96k", ValorDe(argumentos, "-b:a:0"));
    }

    [Fact]
    public void Usa_perfil_mais_alto_apenas_nas_versoes_grandes()
    {
        var argumentos = FfmpegArguments.Transcode("/tmp/a.mp4", "/tmp/saida", Ladder, 30, true);

        Assert.Equal("main", ValorDe(argumentos, "-profile:v:0"));
        Assert.Equal("high", ValorDe(argumentos, "-profile:v:2"));
    }

    [Fact]
    public void Recusa_transcodificar_sem_versoes()
    {
        Assert.Throws<ArgumentException>(() => FfmpegArguments.Transcode("/tmp/a.mp4", "/tmp/saida", [], 30, true));
    }

    [Theory]
    [InlineData("", "/tmp/saida")]
    [InlineData("/tmp/a.mp4", "")]
    public void Recusa_caminhos_vazios(string entrada, string saida)
    {
        Assert.ThrowsAny<ArgumentException>(() => FfmpegArguments.Transcode(entrada, saida, Ladder, 30, true));
    }

    [Fact]
    public void A_miniatura_salta_direto_ao_instante_pedido()
    {
        var argumentos = FfmpegArguments.Thumbnail("/tmp/a.mp4", "/tmp/thumb.jpg", 61.25);

        // O posicionamento precisa vir antes da entrada, senão o FFmpeg decodifica o vídeo
        // inteiro até chegar ao instante.
        Assert.True(argumentos.ToList().IndexOf("-ss") < argumentos.ToList().IndexOf("-i"));
        Assert.Equal("61.25", ValorDe(argumentos, "-ss"));
        Assert.Equal("1", ValorDe(argumentos, "-frames:v"));
        Assert.Equal("scale=640:-2", ValorDe(argumentos, "-vf"));
    }

    [Fact]
    public void A_miniatura_recusa_instante_negativo()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegArguments.Thumbnail("/tmp/a.mp4", "/tmp/t.jpg", -1));
    }

    [Fact]
    public void A_folha_de_miniaturas_usa_a_disposicao_calculada()
    {
        var disposicao = SpriteLayout.For(600, 1920, 1080);

        var filtro = ValorDe(FfmpegArguments.Sprite("/tmp/a.mp4", "/tmp/sprite.jpg", disposicao), "-vf");

        Assert.Contains($"fps=1/{disposicao.IntervalSeconds}", filtro);
        Assert.Contains($"scale={disposicao.ThumbWidth}:{disposicao.ThumbHeight}", filtro);
        Assert.Contains($"tile={SpriteLayout.ColumnCount}x{disposicao.Rows}", filtro);
    }

    [Fact]
    public void Nenhum_argumento_vai_montado_numa_linha_unica()
    {
        // Cada argumento entra separado; se algum trouxesse espaço embutido, um nome de
        // arquivo estranho poderia virar injeção de comando.
        var argumentos = FfmpegArguments.Transcode("/tmp/meu video.mp4", "/tmp/saida", Ladder, 30, true);

        Assert.Contains("/tmp/meu video.mp4", argumentos);
        Assert.DoesNotContain(argumentos, a => a.StartsWith("-i "));
    }
}
