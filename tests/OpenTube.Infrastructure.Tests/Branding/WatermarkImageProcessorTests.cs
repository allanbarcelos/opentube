using OpenTube.Domain.Entities;
using OpenTube.Infrastructure.Branding;
using OpenTube.TestSupport;
using SkiaSharp;

namespace OpenTube.Infrastructure.Tests.Branding;

public class WatermarkImageProcessorTests
{
    private static SKBitmap Ler(byte[] png) => SKBitmap.Decode(png)
        ?? throw new InvalidOperationException("O resultado não é uma imagem legível.");

    /// <summary>Cor do pixel sem o alfa pré-multiplicado, como o navegador a exibe.</summary>
    private static SKColor Cor(SKBitmap imagem, int x, int y) => imagem.GetPixel(x, y);

    [Theory]
    [InlineData(2000, 1000, 640, 320)]  // mesma proporção do padrão
    [InlineData(3000, 200, 640, 43)]    // faixa horizontal: limitada pela largura
    [InlineData(500, 2000, 80, 320)]    // retrato: limitado pela altura
    [InlineData(1024, 1024, 320, 320)]  // quadrado
    public void Reduz_ao_tamanho_padrao_mantendo_a_proporcao(int largura, int altura, int esperadaLargura, int esperadaAltura)
    {
        var resultado = WatermarkImageProcessor.Normalize(PngDeTeste.Criar(largura, altura));

        using var imagem = Ler(resultado);
        Assert.Equal((esperadaLargura, esperadaAltura), (imagem.Width, imagem.Height));
        Assert.Equal((esperadaLargura, esperadaAltura), PlayerWatermark.ReadPngSize(resultado));
    }

    [Theory]
    [InlineData(400, 200)]
    [InlineData(640, 320)]
    [InlineData(160, 40)]
    public void Imagem_que_ja_cabe_nao_e_ampliada(int largura, int altura)
    {
        using var imagem = Ler(WatermarkImageProcessor.Normalize(PngDeTeste.Criar(largura, altura)));

        Assert.Equal((largura, altura), (imagem.Width, imagem.Height));
    }

    [Fact]
    public void Imagem_que_ja_cabe_mantem_os_pixels()
    {
        var original = PngDeTeste.Criar(300, 150, (x, y) => ((byte)x, (byte)y, (byte)(x ^ y), (byte)(x % 2 == 0 ? 255 : 128)));

        using var antes = Ler(original);
        using var depois = Ler(WatermarkImageProcessor.Normalize(original));

        for (var y = 0; y < 150; y += 7)
            for (var x = 0; x < 300; x += 7)
                Assert.Equal(Cor(antes, x, y), Cor(depois, x, y));
    }

    [Fact]
    public void Reducao_preserva_transparencia_e_nao_escurece_as_bordas()
    {
        // Metade esquerda vermelha opaca, metade direita totalmente transparente — mas com a
        // cor "preta" escondida nos pixels transparentes. Um redimensionamento que não trata o
        // alfa mistura esse preto na borda e cria um halo escuro em volta do logotipo.
        var original = PngDeTeste.Criar(2000, 1000, (x, _) => x < 1000 ? ((byte)220, (byte)30, (byte)40, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0));

        using var imagem = Ler(WatermarkImageProcessor.Normalize(original));

        Assert.Equal((640, 320), (imagem.Width, imagem.Height));

        var opaco = Cor(imagem, 100, 160);
        Assert.Equal(new SKColor(220, 30, 40, 255), opaco);
        Assert.Equal(0, Cor(imagem, 540, 160).Alpha);

        // Na borda o alfa cai, mas a cor continua vermelha, e não um vermelho escurecido.
        for (var x = 316; x <= 323; x++)
        {
            var borda = Cor(imagem, x, 160);
            if (borda.Alpha is > 16 and < 255)
                Assert.InRange(borda.Red, 200, 240);
        }
    }

    [Fact]
    public void Reducao_mantem_a_cor_de_areas_uniformes()
    {
        // Listras finas de 1 pixel em preto e branco: reduzidas, viram cinza médio uniforme.
        // Pular pixels em vez de fazer a média resultaria em preto ou branco (serrilhado).
        var original = PngDeTeste.Criar(2560, 1280, (x, _) => x % 2 == 0 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255));

        using var imagem = Ler(WatermarkImageProcessor.Normalize(original));

        for (var x = 50; x < 600; x += 37)
            Assert.InRange(Cor(imagem, x, 160).Red, 100, 155);
    }

    [Fact]
    public void Recusa_imagem_pequena_demais()
    {
        var erro = Assert.Throws<ArgumentException>(() => WatermarkImageProcessor.Normalize(PngDeTeste.Criar(150, 80)));

        Assert.Equal("The image must be at least 160 pixels on its longest side.", erro.Message);
    }

    [Fact]
    public void Recusa_arquivo_que_nao_e_png()
    {
        var erro = Assert.Throws<ArgumentException>(() => WatermarkImageProcessor.Normalize("não é uma imagem"u8.ToArray()));

        Assert.Equal("The file is not a PNG image.", erro.Message);
    }

    [Fact]
    public void Recusa_png_corrompido_depois_do_cabecalho()
    {
        var png = PngDeTeste.Criar(400, 200);
        var truncado = png[..40];

        Assert.Throws<ArgumentException>(() => WatermarkImageProcessor.Normalize(truncado));
    }

    [Fact]
    public void Recusa_dimensoes_enormes_antes_de_decodificar()
    {
        // Só o cabeçalho declara 20000 × 20000: decodificar alocaria 1,6 GB.
        var png = PngDeTeste.Criar(400, 200);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 20000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 20000);

        var erro = Assert.Throws<ArgumentException>(() => WatermarkImageProcessor.Normalize(png));

        Assert.Equal("The image must be at most 4096 pixels on each side.", erro.Message);
    }

    [Fact]
    public void Recusa_arquivo_maior_que_o_limite_de_envio()
    {
        var grande = new byte[WatermarkImageProcessor.MaxUploadBytes + 1];

        var erro = Assert.Throws<ArgumentException>(() => WatermarkImageProcessor.Normalize(grande));

        Assert.Equal("The image is larger than 5 MB.", erro.Message);
    }
}
