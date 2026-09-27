namespace OpenTube.Domain.Entities;

/// <summary>
/// Um worker que informou se consegue transcrever. É por aqui que a aplicação sabe se há
/// Whisper em algum lugar: o botão de legenda automática só aparece enquanto algum worker
/// disser que sim, e o disser há pouco.
/// </summary>
public class TranscriptionWorker
{
    /// <summary>Depois disso sem notícia, o worker é considerado fora do ar.</summary>
    public static readonly TimeSpan Validity = TimeSpan.FromMinutes(2);

    private TranscriptionWorker() { }

    public string WorkerId { get; private set; } = string.Empty;

    public bool Available { get; private set; }

    /// <summary>Descrição do que faz a transcrição ("whisper.cpp · CUDA · large-v3-turbo").</summary>
    public string? Engine { get; private set; }

    public DateTimeOffset CheckedAt { get; private set; }

    public bool IsCurrentAt(DateTimeOffset now) => now - CheckedAt < Validity;

    public static TranscriptionWorker Report(string workerId, bool available, string? engine, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        return new TranscriptionWorker
        {
            WorkerId = workerId.Trim(),
            Available = available,
            Engine = string.IsNullOrWhiteSpace(engine) ? null : engine.Trim()[..Math.Min(engine.Trim().Length, 200)],
            CheckedAt = now
        };
    }
}
