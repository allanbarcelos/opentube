using System.Net.Http.Headers;
using OpenTube.Infrastructure.Storage;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Storage;

[Collection(IntegrationCollection.Name)]
public class S3VideoStorageTests(MinioFixture fixture)
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task Cria_os_buckets_sem_reclamar_se_ja_existirem()
    {
        using var storage = fixture.CreateStorage();

        await storage.EnsureBucketsAsync();
        await storage.EnsureBucketsAsync();

        await storage.PutTextAsync(StorageBucket.Vod, "teste/ok.txt", "conteúdo", "text/plain");
        Assert.True(await storage.ExistsAsync(StorageBucket.Vod, "teste/ok.txt"));
    }

    [Fact]
    public async Task Grava_e_le_texto()
    {
        using var storage = fixture.CreateStorage();
        var chave = $"{Guid.CreateVersion7()}/master.m3u8";

        await storage.PutTextAsync(StorageBucket.Vod, chave, "#EXTM3U\n", "application/vnd.apple.mpegurl");

        Assert.Equal("#EXTM3U\n", await storage.GetTextAsync(StorageBucket.Vod, chave));
    }

    [Fact]
    public async Task Envia_e_baixa_arquivo_local()
    {
        using var storage = fixture.CreateStorage();
        var origem = Path.GetTempFileName();
        var destino = Path.Combine(Path.GetTempPath(), Guid.CreateVersion7().ToString(), "baixado.bin");
        var conteudo = new byte[4096];
        Random.Shared.NextBytes(conteudo);
        await File.WriteAllBytesAsync(origem, conteudo);

        try
        {
            var chave = $"{Guid.CreateVersion7()}/arquivo.bin";
            await storage.PutFileAsync(StorageBucket.Vod, chave, origem, "application/octet-stream");
            await storage.GetFileAsync(StorageBucket.Vod, chave, destino);

            Assert.Equal(conteudo, await File.ReadAllBytesAsync(destino));
            Assert.Equal(4096, await storage.GetSizeAsync(StorageBucket.Vod, chave));
        }
        finally
        {
            File.Delete(origem);
            if (File.Exists(destino))
                File.Delete(destino);
        }
    }

    [Fact]
    public async Task Objeto_inexistente_nao_existe_e_nao_tem_tamanho()
    {
        using var storage = fixture.CreateStorage();

        Assert.False(await storage.ExistsAsync(StorageBucket.Vod, "nao/existe.txt"));
        Assert.Null(await storage.GetSizeAsync(StorageBucket.Vod, "nao/existe.txt"));
    }

    [Fact]
    public async Task Envio_multipart_completo_pelo_navegador()
    {
        using var storage = fixture.CreateStorage();
        var chave = StorageKeys.Original(Guid.CreateVersion7(), "reuniao.mp4");

        // Dois pedaços de 5 MiB: é o mínimo que o protocolo aceita fora do último pedaço.
        var pedaco1 = new byte[5 * 1024 * 1024];
        var pedaco2 = new byte[1024];
        Random.Shared.NextBytes(pedaco1);
        Random.Shared.NextBytes(pedaco2);

        var uploadId = await storage.StartUploadAsync(chave, "video/mp4");
        var urls = storage.SignUploadParts(chave, uploadId, firstPart: 1, partCount: 2);

        Assert.Equal([1, 2], urls.Select(u => u.PartNumber));

        var enviados = new List<CompletedPart>();
        foreach (var (url, dados) in urls.Zip(new[] { pedaco1, pedaco2 }, (u, d) => (u.Url, d)))
        {
            var conteudo = new ByteArrayContent(dados);
            conteudo.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var resposta = await Http.PutAsync(url, conteudo);
            resposta.EnsureSuccessStatusCode();

            var etag = resposta.Headers.ETag!.Tag;
            enviados.Add(new CompletedPart(urls[enviados.Count].PartNumber, etag));
        }

        var tamanho = await storage.CompleteUploadAsync(chave, uploadId, enviados);

        Assert.Equal(pedaco1.Length + pedaco2.Length, tamanho);
        Assert.True(await storage.ExistsAsync(StorageBucket.Originals, chave));
    }

    [Fact]
    public async Task Cancela_um_envio_interrompido()
    {
        using var storage = fixture.CreateStorage();
        var chave = StorageKeys.Original(Guid.CreateVersion7(), "abandonado.mp4");

        var uploadId = await storage.StartUploadAsync(chave, "video/mp4");
        await storage.AbortUploadAsync(chave, uploadId);

        Assert.False(await storage.ExistsAsync(StorageBucket.Originals, chave));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void Recusa_faixas_de_pedacos_invalidas(int primeiro, int quantidade)
    {
        using var storage = fixture.CreateStorage();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            storage.SignUploadParts("chave", "upload", primeiro, quantidade));
    }

    [Fact]
    public void Recusa_passar_do_limite_de_pedacos_do_protocolo()
    {
        using var storage = fixture.CreateStorage();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            storage.SignUploadParts("chave", "upload", firstPart: 9_999, partCount: 5));
    }

    [Fact]
    public async Task Url_assinada_permite_ler_o_objeto_sem_credencial()
    {
        using var storage = fixture.CreateStorage();
        var chave = $"{Guid.CreateVersion7()}/master.m3u8";
        await storage.PutTextAsync(StorageBucket.Vod, chave, "#EXTM3U\n#EXT-X-VERSION:7\n", "application/vnd.apple.mpegurl");

        var url = storage.SignDownloadUrl(StorageBucket.Vod, chave, TimeSpan.FromMinutes(5));
        var resposta = await Http.GetAsync(url);

        Assert.True(resposta.IsSuccessStatusCode);
        Assert.Contains("#EXTM3U", await resposta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Objeto_nao_e_acessivel_sem_assinatura()
    {
        using var storage = fixture.CreateStorage();
        var chave = $"{Guid.CreateVersion7()}/privado.m3u8";
        await storage.PutTextAsync(StorageBucket.Vod, chave, "#EXTM3U", "application/vnd.apple.mpegurl");

        var url = storage.SignDownloadUrl(StorageBucket.Vod, chave, TimeSpan.FromMinutes(5));
        var semAssinatura = url[..url.IndexOf('?')];

        var resposta = await Http.GetAsync(semAssinatura);

        Assert.False(resposta.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Lista_e_apaga_tudo_sob_um_prefixo()
    {
        using var storage = fixture.CreateStorage();
        var videoId = Guid.CreateVersion7();
        var prefixo = StorageKeys.VodPrefix(videoId);

        await storage.PutTextAsync(StorageBucket.Vod, StorageKeys.Master(videoId), "#EXTM3U", "application/vnd.apple.mpegurl");
        await storage.PutTextAsync(StorageBucket.Vod, StorageKeys.RenditionPlaylist(videoId, "720p"), "#EXTM3U", "application/vnd.apple.mpegurl");
        await storage.PutTextAsync(StorageBucket.Vod, StorageKeys.Caption(videoId, "pt-BR"), "WEBVTT", "text/vtt");

        var chaves = await storage.ListAsync(StorageBucket.Vod, prefixo);
        Assert.Equal(3, chaves.Count);

        var apagados = await storage.DeletePrefixAsync(StorageBucket.Vod, prefixo);

        Assert.Equal(3, apagados);
        Assert.Empty(await storage.ListAsync(StorageBucket.Vod, prefixo));
    }

    [Fact]
    public async Task Apagar_prefixo_vazio_nao_e_erro()
    {
        using var storage = fixture.CreateStorage();

        Assert.Equal(0, await storage.DeletePrefixAsync(StorageBucket.Vod, $"{Guid.CreateVersion7()}/"));
    }

    [Fact]
    public async Task Apagar_exige_prefixo_para_nao_esvaziar_o_bucket_inteiro()
    {
        using var storage = fixture.CreateStorage();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => storage.DeletePrefixAsync(StorageBucket.Vod, "   "));
    }

    [Fact]
    public async Task Separa_os_buckets_de_original_e_de_reproducao()
    {
        using var storage = fixture.CreateStorage();
        var chave = $"{Guid.CreateVersion7()}/arquivo.txt";

        await storage.PutTextAsync(StorageBucket.Originals, chave, "original", "text/plain");

        Assert.True(await storage.ExistsAsync(StorageBucket.Originals, chave));
        Assert.False(await storage.ExistsAsync(StorageBucket.Vod, chave));
    }
}
