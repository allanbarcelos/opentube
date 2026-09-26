using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace OpenTube.TestSupport;

/// <summary>
/// Gera um PNG válido de verdade — assinatura, cabeçalho, dados comprimidos e CRC —, para
/// testes que precisam de uma imagem que qualquer leitor de PNG aceite.
/// </summary>
public static class PngDeTeste
{
    /// <summary>
    /// PNG RGBA. Sem <paramref name="pixel"/>, é branco semitransparente; com ele, cada pixel
    /// vem da função, o que permite desenhar áreas para conferir o redimensionamento.
    /// </summary>
    public static byte[] Criar(int largura = 400, int altura = 200, Func<int, int, (byte R, byte G, byte B, byte A)>? pixel = null)
    {
        using var saida = new MemoryStream();
        saida.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var cabecalho = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(cabecalho.AsSpan(0), (uint)largura);
        BinaryPrimitives.WriteUInt32BigEndian(cabecalho.AsSpan(4), (uint)altura);
        cabecalho[8] = 8;  // 8 bits por canal
        cabecalho[9] = 6;  // RGBA
        EscreverBloco(saida, "IHDR", cabecalho);

        // Cada linha começa com o byte de filtro (0) seguido dos pixels RGBA.
        var linhas = new byte[altura * (1 + largura * 4)];
        for (var y = 0; y < altura; y++)
        {
            for (var x = 0; x < largura; x++)
            {
                var i = y * (1 + largura * 4) + 1 + x * 4;
                var (r, g, b, a) = pixel?.Invoke(x, y) ?? ((byte)255, (byte)255, (byte)255, (byte)200);
                linhas[i] = r;
                linhas[i + 1] = g;
                linhas[i + 2] = b;
                linhas[i + 3] = a;
            }
        }

        using (var comprimido = new MemoryStream())
        {
            using (var zlib = new ZLibStream(comprimido, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(linhas);

            EscreverBloco(saida, "IDAT", comprimido.ToArray());
        }

        EscreverBloco(saida, "IEND", []);

        return saida.ToArray();
    }

    private static void EscreverBloco(Stream saida, string tipo, byte[] dados)
    {
        Span<byte> numero = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(numero, (uint)dados.Length);
        saida.Write(numero);

        var tipoEDados = Encoding.ASCII.GetBytes(tipo).Concat(dados).ToArray();
        saida.Write(tipoEDados);

        BinaryPrimitives.WriteUInt32BigEndian(numero, Crc32(tipoEDados));
        saida.Write(numero);
    }

    private static uint Crc32(byte[] dados)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var b in dados)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }

        return ~crc;
    }
}
