using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Entities;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Transcription;

/// <summary>Situação da transcrição automática, como a aplicação a vê.</summary>
/// <param name="Available">Se há algum worker com Whisper respondendo agora.</param>
/// <param name="Engine">O que transcreve, quando disponível.</param>
public sealed record TranscriptionState(bool Available, string? Engine)
{
    public static TranscriptionState Unavailable { get; } = new(false, null);
}

/// <summary>
/// Onde os workers informam se conseguem transcrever e onde a aplicação consulta isso. O
/// worker roda em outro processo, às vezes em outra máquina: o banco é o que os dois veem.
/// </summary>
public class TranscriptionAvailability(OpenTubeDbContext db, TimeProvider clock)
{
    public async Task<TranscriptionState> GetAsync(CancellationToken cancellationToken = default)
    {
        var limite = clock.GetUtcNow() - TranscriptionWorker.Validity;

        var ativo = await db.TranscriptionWorkers
            .AsNoTracking()
            .Where(w => w.Available && w.CheckedAt > limite)
            .OrderByDescending(w => w.CheckedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return ativo is null ? TranscriptionState.Unavailable : new TranscriptionState(true, ativo.Engine);
    }

    /// <summary>Registro do worker: grava ou atualiza a própria linha, numa instrução só.</summary>
    public async Task ReportAsync(string workerId, bool available, string? engine, CancellationToken cancellationToken = default)
    {
        var relato = TranscriptionWorker.Report(workerId, available, engine, clock.GetUtcNow());

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO transcription_workers (worker_id, available, engine, checked_at)
            VALUES ({relato.WorkerId}, {relato.Available}, {relato.Engine}, {relato.CheckedAt})
            ON CONFLICT (worker_id) DO UPDATE
               SET available = EXCLUDED.available, engine = EXCLUDED.engine, checked_at = EXCLUDED.checked_at
            """, cancellationToken);
    }
}
