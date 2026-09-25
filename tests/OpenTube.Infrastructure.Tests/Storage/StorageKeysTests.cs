using OpenTube.Infrastructure.Storage;

namespace OpenTube.Infrastructure.Tests.Storage;

public class StorageKeysTests
{
    private static readonly Guid Video = Guid.Parse("0199a0b0-0000-7000-8000-000000000001");

    [Fact]
    public void Monta_o_caminho_do_arquivo_original_preservando_a_extensao()
    {
        Assert.Equal($"{Video}/source.mp4", StorageKeys.Original(Video, "Reunião Trimestral.MP4"));
    }

    [Theory]
    [InlineData("video.mkv", ".mkv")]
    [InlineData("VIDEO.MOV", ".mov")]
    [InlineData("arquivo.webm", ".webm")]
    public void Normaliza_a_extensao_para_minusculas(string nome, string esperado)
    {
        Assert.Equal(esperado, StorageKeys.SafeExtension(nome));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sem-extensao")]
    [InlineData("arquivo.")]
    [InlineData("arquivo.extensao-absurdamente-longa")]
    public void Cai_para_extensao_generica_quando_nao_da_para_confiar_no_nome(string? nome)
    {
        Assert.Equal(".bin", StorageKeys.SafeExtension(nome));
    }

    [Theory]
    [InlineData("video.mp4/../../etc/passwd")]
    [InlineData("video.mp 4")]
    [InlineData("video.m/p4")]
    public void Nao_deixa_o_nome_enviado_pelo_navegador_virar_caminho(string nome)
    {
        var chave = StorageKeys.Original(Video, nome);

        Assert.DoesNotContain("..", chave);
        Assert.Equal(1, chave.Count(c => c == '/'));
        Assert.StartsWith($"{Video}/source", chave);
    }

    [Fact]
    public void Monta_os_caminhos_das_saidas_de_reproducao()
    {
        Assert.Equal($"{Video}/", StorageKeys.VodPrefix(Video));
        Assert.Equal($"{Video}/master.m3u8", StorageKeys.Master(Video));
        Assert.Equal($"{Video}/720p/stream.m3u8", StorageKeys.RenditionPlaylist(Video, "720p"));
        Assert.Equal($"{Video}/720p/", StorageKeys.RenditionPrefix(Video, "720p"));
        Assert.Equal($"{Video}/thumb.jpg", StorageKeys.Thumbnail(Video));
        Assert.Equal($"{Video}/sprite.jpg", StorageKeys.Sprite(Video));
        Assert.Equal($"{Video}/sprite.vtt", StorageKeys.SpriteMetadata(Video));
        Assert.Equal($"{Video}/captions/pt-br.vtt", StorageKeys.Caption(Video, "pt-BR"));
    }

    [Theory]
    [InlineData("../../segredo")]
    [InlineData("720p/../..")]
    [InlineData("")]
    public void Limpa_o_nome_da_versao_antes_de_usar_no_caminho(string versao)
    {
        var chave = StorageKeys.RenditionPlaylist(Video, versao);

        Assert.DoesNotContain("..", chave);
        Assert.Equal(2, chave.Count(c => c == '/'));
    }

    [Fact]
    public void Todos_os_caminhos_de_um_video_ficam_sob_o_mesmo_prefixo()
    {
        var prefixo = StorageKeys.VodPrefix(Video);

        Assert.StartsWith(prefixo, StorageKeys.Master(Video));
        Assert.StartsWith(prefixo, StorageKeys.Thumbnail(Video));
        Assert.StartsWith(prefixo, StorageKeys.Caption(Video, "pt-BR"));
        Assert.StartsWith(prefixo, StorageKeys.RenditionPrefix(Video, "1080p"));
        Assert.StartsWith(prefixo, StorageKeys.OutputPrefix(Video, Guid.CreateVersion7()));
    }

    [Fact]
    public void Cada_geracao_de_saidas_tem_pasta_propria_fora_das_legendas()
    {
        var geracao = Guid.CreateVersion7();
        var prefixo = StorageKeys.OutputPrefix(Video, geracao);

        Assert.Equal($"{Video}/r-{geracao:n}/", prefixo);
        Assert.NotEqual(prefixo, StorageKeys.OutputPrefix(Video, Guid.CreateVersion7()));
        Assert.Equal($"{prefixo}master.m3u8", StorageKeys.MasterUnder(prefixo));
        Assert.Equal($"{prefixo}720p/stream.m3u8", StorageKeys.RenditionPlaylistUnder(prefixo, "720p"));
        Assert.Equal($"{prefixo}thumb.jpg", StorageKeys.ThumbnailUnder(prefixo));
        Assert.DoesNotContain(prefixo, StorageKeys.Caption(Video, "pt"));
        Assert.StartsWith(StorageKeys.CaptionsPrefix(Video), StorageKeys.Caption(Video, "pt"));
    }
}
