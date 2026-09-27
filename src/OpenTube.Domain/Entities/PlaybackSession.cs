// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Analytics;
using OpenTube.Domain.Enums;

namespace OpenTube.Domain.Entities;

/// <summary>
/// Uma sessão de reprodução: uma pessoa, um vídeo, uma sentada. Guarda os trechos realmente
/// assistidos, já fundidos, porque é a diferença entre "viu dez minutos" e "viu o mesmo
/// minuto dez vezes".
/// </summary>
public class PlaybackSession
{
    private readonly List<PlaybackInterval> _intervals = [];

    private PlaybackSession() { }

    public Guid Id { get; private set; }
    public Guid VideoId { get; private set; }

    /// <summary>Pessoa autenticada, quando houver.</summary>
    public Guid? UserId { get; private set; }

    /// <summary>Identificador do visitante anônimo, guardado no navegador dele.</summary>
    public string? AnonymousId { get; private set; }

    /// <summary>Concessão que liberou esta reprodução, quando o acesso veio de uma.</summary>
    public Guid? GrantId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }

    public DeviceType Device { get; private set; }
    public string OperatingSystem { get; private set; } = UserAgentParser.Desconhecido;
    public string Browser { get; private set; } = UserAgentParser.Desconhecido;

    /// <summary>Resumo do endereço de origem; o endereço em si não é guardado.</summary>
    public string? IpHash { get; private set; }

    public string? Country { get; private set; }
    public string? Referrer { get; private set; }

    /// <summary>Maior qualidade que o player chegou a usar.</summary>
    public string? MaxQuality { get; private set; }

    /// <summary>Ponto mais adiante alcançado, mesmo por salto.</summary>
    public double FurthestPosition { get; private set; }

    /// <summary>Segundos únicos assistidos, recalculados a cada batida.</summary>
    public double WatchedSeconds { get; private set; }

    public bool Completed { get; private set; }

    public int ErrorCount { get; private set; }

    public IReadOnlyCollection<PlaybackInterval> Intervals => _intervals;

    public static PlaybackSession Start(
        Guid videoId,
        DateTimeOffset now,
        Guid? userId = null,
        string? anonymousId = null,
        Guid? grantId = null,
        ClientProfile client = default,
        string? ipHash = null,
        string? country = null,
        string? referrer = null)
    {
        if (userId is null && string.IsNullOrWhiteSpace(anonymousId))
            throw new ArgumentException("A sessão precisa de uma pessoa ou de um identificador de visitante.", nameof(anonymousId));

        return new PlaybackSession
        {
            Id = Guid.CreateVersion7(),
            VideoId = videoId,
            UserId = userId,
            AnonymousId = userId is null ? anonymousId!.Trim() : null,
            GrantId = grantId,
            StartedAt = now,
            LastSeenAt = now,
            Device = client.Device,
            OperatingSystem = client.OperatingSystem ?? UserAgentParser.Desconhecido,
            Browser = client.Browser ?? UserAgentParser.Desconhecido,
            IpHash = ipHash,
            Country = country,
            Referrer = Truncate(referrer, 500)
        };
    }

    /// <summary>
    /// Registra um trecho assistido. O conjunto é mantido fundido: sem isso, uma reprodução
    /// longa acumularia uma linha por batida e o total assistido ficaria inflado.
    /// </summary>
    public void Record(WatchInterval interval, DateTimeOffset now, double videoDuration)
    {
        LastSeenAt = now;

        if (interval.IsEmpty)
            return;

        var fundidos = IntervalMerger.Merge(
            _intervals.Select(i => new WatchInterval(i.StartSeconds, i.EndSeconds)).Append(interval));

        _intervals.Clear();
        _intervals.AddRange(fundidos.Select(i => PlaybackInterval.Create(Id, i.Start, i.End)));

        WatchedSeconds = fundidos.Sum(i => i.Duration);
        FurthestPosition = Math.Max(FurthestPosition, interval.End);

        // "Assistiu até o fim" com folga: os últimos segundos costumam ficar de fora por
        // causa dos créditos ou de um corte na última batida. A folga é proporcional, senão
        // num vídeo de oito segundos parar no meio já contaria como concluído.
        if (videoDuration > 0 && FurthestPosition >= videoDuration - CompletionTolerance(videoDuration))
            Completed = true;
    }

    /// <summary>Anota o ponto alcançado por um salto, que não conta como assistido.</summary>
    public void Seek(double position, DateTimeOffset now)
    {
        LastSeenAt = now;
        FurthestPosition = Math.Max(FurthestPosition, Math.Max(0, position));
    }

    public void RecordQuality(string? quality, DateTimeOffset now)
    {
        LastSeenAt = now;

        if (string.IsNullOrWhiteSpace(quality))
            return;

        // Compara pela altura anunciada no nome ("720p"), e não por texto.
        if (MaxQuality is null || Altura(quality) > Altura(MaxQuality))
            MaxQuality = quality.Trim();
    }

    public void RecordError(DateTimeOffset now)
    {
        LastSeenAt = now;
        ErrorCount++;
    }

    public void MarkCompleted(DateTimeOffset now)
    {
        LastSeenAt = now;
        Completed = true;
    }

    public void End(DateTimeOffset now)
    {
        LastSeenAt = now;
        EndedAt ??= now;
    }

    /// <summary>Trechos assistidos no formato usado pelos cálculos.</summary>
    public IReadOnlyList<WatchInterval> ToWatchIntervals() =>
        [.. _intervals.Select(i => new WatchInterval(i.StartSeconds, i.EndSeconds))];

    /// <summary>
    /// Folga aceita para considerar o vídeo assistido até o fim: cinco por cento da duração,
    /// limitada a cinco segundos.
    /// </summary>
    public static double CompletionTolerance(double videoDuration) =>
        Math.Min(5, Math.Max(0, videoDuration) * 0.05);

    private static int Altura(string qualidade)
    {
        var digitos = new string(qualidade.TakeWhile(char.IsAsciiDigit).ToArray());

        return int.TryParse(digitos, out var altura) ? altura : 0;
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}

/// <summary>Um trecho assistido dentro de uma sessão, já fundido com os vizinhos.</summary>
public class PlaybackInterval
{
    private PlaybackInterval() { }

    public long Id { get; private set; }
    public Guid SessionId { get; private set; }
    public double StartSeconds { get; private set; }
    public double EndSeconds { get; private set; }

    public static PlaybackInterval Create(Guid sessionId, double start, double end) => new()
    {
        SessionId = sessionId,
        StartSeconds = start,
        EndSeconds = end
    };
}
