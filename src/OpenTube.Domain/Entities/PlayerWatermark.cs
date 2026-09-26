using System.Buffers.Binary;
using OpenTube.Domain.Enums;

namespace OpenTube.Domain.Entities;

/// <summary>
/// Marca d'água do acervo: uma imagem PNG definida pela administração, exibida sobre todos os
/// vídeos na posição escolhida. Existe no máximo uma; sem ela, nenhuma imagem é exibida.
/// </summary>
public class PlayerWatermark
{
    /// <summary>Identificador fixo: a marca é uma configuração única do acervo.</summary>
    public const int SingletonId = 1;

    /// <summary>
    /// Tamanho padrão: a imagem guardada cabe em 640 × 320, mantendo a proporção. Na tela ela
    /// ocupa até 16% da largura do player — uns 600 px numa tela cheia 4K —, então 640 px
    /// mantém a nitidez até lá, inclusive em telas de alta densidade, sem inflar o arquivo.
    /// A altura de 320 acomoda logotipos quadrados e horizontais.
    /// </summary>
    public const int StandardWidth = 640;

    public const int StandardHeight = 320;

    /// <summary>
    /// Menor lado maior aceito. Imagem menor não é ampliada — ampliar é o que perde qualidade —
    /// e abaixo disso ficaria borrada na tela cheia.
    /// </summary>
    public const int MinLongestSide = 160;

    /// <summary>Tamanho máximo da imagem guardada, já no tamanho padrão.</summary>
    public const int MaxBytes = 1024 * 1024;

    public const string ContentType = "image/png";

    private static ReadOnlySpan<byte> AssinaturaPng => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private PlayerWatermark() { }

    public int Id { get; private set; }

    public byte[] Image { get; private set; } = [];

    public int Width { get; private set; }
    public int Height { get; private set; }

    public WatermarkPosition Position { get; private set; }

    public Guid UpdatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Muda a cada troca de imagem ou de posição. Vai no endereço da imagem, que pode então
    /// ficar em cache por muito tempo sem que uma troca demore a aparecer.
    /// </summary>
    public long Version => UpdatedAt.ToUnixTimeMilliseconds();

    public static PlayerWatermark Define(byte[] image, WatermarkPosition position, Guid adminId, DateTimeOffset now)
    {
        var (largura, altura) = ValidarImagemGuardada(image);

        return new PlayerWatermark
        {
            Id = SingletonId,
            Image = image,
            Width = largura,
            Height = altura,
            Position = ValidarPosicao(position),
            UpdatedBy = adminId,
            UpdatedAt = now
        };
    }

    /// <summary>Troca a imagem mantendo a posição, ou trocando-a junto.</summary>
    public void Replace(byte[] image, WatermarkPosition position, Guid adminId, DateTimeOffset now)
    {
        var (largura, altura) = ValidarImagemGuardada(image);

        Image = image;
        Width = largura;
        Height = altura;
        Position = ValidarPosicao(position);
        UpdatedBy = adminId;
        UpdatedAt = now;
    }

    public void MoveTo(WatermarkPosition position, Guid adminId, DateTimeOffset now)
    {
        Position = ValidarPosicao(position);
        UpdatedBy = adminId;
        UpdatedAt = now;
    }

    /// <summary>
    /// Confere que o arquivo é mesmo um PNG — pela assinatura e pelo cabeçalho, não pela
    /// extensão ou pelo tipo informado pelo navegador — e devolve as dimensões. Não impõe
    /// tamanho: serve também para o arquivo original, antes de ser redimensionado.
    /// </summary>
    public static (int Width, int Height) ReadPngSize(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (image.Length == 0)
            throw new ArgumentException("Choose a PNG image.");

        // Assinatura (8 bytes), tamanho do bloco (4), "IHDR" (4), largura (4) e altura (4).
        if (image.Length < 24 || !image.AsSpan(0, 8).SequenceEqual(AssinaturaPng)
            || !image.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new ArgumentException("The file is not a PNG image.");

        var largura = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(16, 4));
        var altura = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(20, 4));

        if (largura == 0 || altura == 0 || largura > int.MaxValue || altura > int.MaxValue)
            throw new ArgumentException("The file is not a PNG image.");

        return ((int)largura, (int)altura);
    }

    /// <summary>
    /// Regras da imagem guardada: PNG, já no tamanho padrão e com lado maior suficiente. O
    /// redimensionamento acontece antes, fora do domínio; aqui só se garante o resultado.
    /// </summary>
    private static (int Width, int Height) ValidarImagemGuardada(byte[] image)
    {
        var (largura, altura) = ReadPngSize(image);

        if (image.Length > MaxBytes)
            throw new ArgumentException("The image is larger than 1 MB.");

        if (largura > StandardWidth || altura > StandardHeight)
            throw new ArgumentException("The image must be resized to the standard watermark size first.");

        if (Math.Max(largura, altura) < MinLongestSide)
            throw new ArgumentException("The image must be at least 160 pixels on its longest side.");

        return (largura, altura);
    }

    private static WatermarkPosition ValidarPosicao(WatermarkPosition position) =>
        Enum.IsDefined(position) ? position : throw new ArgumentException("Choose where the watermark goes.");
}
