using System.Globalization;
using System.Text;

namespace OpenTube.Worker.Media;

/// <summary>
/// Disposição da folha de miniaturas usada na prévia da barra de progresso.
/// </summary>
/// <param name="IntervalSeconds">Intervalo entre miniaturas.</param>
/// <param name="Count">Quantidade de miniaturas.</param>
/// <param name="Columns">Miniaturas por linha.</param>
/// <param name="Rows">Quantidade de linhas.</param>
/// <param name="ThumbWidth">Largura de cada miniatura.</param>
/// <param name="ThumbHeight">Altura de cada miniatura.</param>
public readonly record struct SpriteLayout(
    double IntervalSeconds,
    int Count,
    int Columns,
    int Rows,
    int ThumbWidth,
    int ThumbHeight)
{
    /// <summary>Quantas miniaturas cabem numa linha da folha.</summary>
    public const int ColumnCount = 10;

    /// <summary>Teto de miniaturas: além disso a folha fica grande demais para baixar de uma vez.</summary>
    public const int MaxThumbs = 100;

    public const int DefaultThumbWidth = 160;

    /// <summary>Calcula a disposição para um vídeo com a duração e a proporção informadas.</summary>
    public static SpriteLayout For(double durationSeconds, int sourceWidth, int sourceHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(durationSeconds, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceHeight, 0);

        // Vídeo curto ganha uma miniatura a cada dois segundos; vídeo longo espaça o
        // bastante para não passar do teto.
        var intervalo = Math.Max(2, Math.Ceiling(durationSeconds / MaxThumbs));
        var quantidade = Math.Max(1, (int)Math.Ceiling(durationSeconds / intervalo));

        var largura = DefaultThumbWidth;
        var altura = Par(largura * sourceHeight / (double)sourceWidth);

        return new SpriteLayout(
            intervalo,
            quantidade,
            Math.Min(ColumnCount, quantidade),
            (int)Math.Ceiling(quantidade / (double)ColumnCount),
            largura,
            altura);
    }

    /// <summary>Posição de uma miniatura dentro da folha.</summary>
    public (int X, int Y) PositionOf(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

        return (index % ColumnCount * ThumbWidth, index / ColumnCount * ThumbHeight);
    }

    private static int Par(double valor)
    {
        var arredondado = (int)Math.Round(valor, MidpointRounding.AwayFromZero);
        if (arredondado % 2 != 0)
            arredondado++;

        return Math.Max(2, arredondado);
    }
}

/// <summary>Monta o arquivo WebVTT que liga cada instante do vídeo a um recorte da folha.</summary>
public static class SpriteVtt
{
    public static string Build(SpriteLayout layout, string spriteFileName, double durationSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spriteFileName);

        var vtt = new StringBuilder("WEBVTT\n\n");

        for (var i = 0; i < layout.Count; i++)
        {
            var inicio = i * layout.IntervalSeconds;
            var fim = Math.Min((i + 1) * layout.IntervalSeconds, durationSeconds);

            if (inicio >= durationSeconds)
                break;

            var (x, y) = layout.PositionOf(i);

            vtt.Append(Tempo(inicio)).Append(" --> ").Append(Tempo(fim)).Append('\n');
            vtt.Append(spriteFileName)
               .Append("#xywh=")
               .Append(x).Append(',')
               .Append(y).Append(',')
               .Append(layout.ThumbWidth).Append(',')
               .Append(layout.ThumbHeight)
               .Append("\n\n");
        }

        return vtt.ToString();
    }

    /// <summary>Formata o instante no padrão do WebVTT: <c>hh:mm:ss.mmm</c>.</summary>
    public static string Tempo(double segundos)
    {
        var tempo = TimeSpan.FromSeconds(Math.Max(0, segundos));

        return string.Create(CultureInfo.InvariantCulture,
            $"{(int)tempo.TotalHours:D2}:{tempo.Minutes:D2}:{tempo.Seconds:D2}.{tempo.Milliseconds:D3}");
    }
}
