using OpenTube.Domain.Enums;

namespace OpenTube.Domain.Entities;

/// <summary>
/// Evento bruto relatado pelo player. Fica numa tabela particionada por mês, porque é a única
/// que cresce sem parar e a única que um dia precisará ser descartada por idade.
/// </summary>
public class PlaybackEvent
{
    private PlaybackEvent() { }

    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    public Guid VideoId { get; private set; }
    public Guid? UserId { get; private set; }

    public PlaybackEventType Type { get; private set; }

    /// <summary>Instante do evento. É também a chave de particionamento.</summary>
    public DateTimeOffset At { get; private set; }

    /// <summary>Posição no vídeo, em segundos.</summary>
    public double Position { get; private set; }

    /// <summary>Começo do trecho relatado, nas batidas de progresso.</summary>
    public double? FromSeconds { get; private set; }

    /// <summary>Fim do trecho relatado, nas batidas de progresso.</summary>
    public double? ToSeconds { get; private set; }

    /// <summary>Detalhe livre do evento, como a qualidade escolhida ou o código do erro.</summary>
    public string? Detail { get; private set; }

    public static PlaybackEvent Create(
        Guid sessionId,
        Guid videoId,
        Guid? userId,
        PlaybackEventType type,
        DateTimeOffset at,
        double position,
        double? fromSeconds = null,
        double? toSeconds = null,
        string? detail = null) => new()
    {
        Id = Guid.CreateVersion7(),
        SessionId = sessionId,
        VideoId = videoId,
        UserId = userId,
        Type = type,
        At = at,
        Position = Math.Max(0, position),
        FromSeconds = fromSeconds,
        ToSeconds = toSeconds,
        Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim()[..Math.Min(detail.Trim().Length, 200)]
    };
}
