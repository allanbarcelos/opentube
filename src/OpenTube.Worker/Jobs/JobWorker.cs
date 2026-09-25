using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Queue;

namespace OpenTube.Worker.Jobs;

/// <summary>Ajustes do laço de consumo da fila.</summary>
public class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>Identificação deste worker nos registros de reserva.</summary>
    public string WorkerId { get; set; } = $"{Environment.MachineName}-{Environment.ProcessId}";

    /// <summary>Espera entre consultas quando a fila está vazia.</summary>
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Prazo da reserva de um trabalho, renovado enquanto ele executa.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Intervalo de renovação da reserva.</summary>
    public TimeSpan RenewInterval { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Consome a fila de trabalhos. Roda em processo separado da aplicação porque é a única parte
/// do sistema que consome CPU de forma intensa e precisa escalar por conta própria.
/// </summary>
public class JobWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> options,
    ILogger<JobWorker> logger) : BackgroundService
{
    private readonly WorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker {WorkerId} iniciado", _options.WorkerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            bool trabalhou;

            try
            {
                trabalhou = await ProcessarProximoAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // Uma falha ao falar com o banco não pode derrubar o laço: o worker espera
                // e tenta de novo, senão uma indisponibilidade momentânea exigiria reinício.
                logger.LogError(e, "Falha ao consultar a fila");
                trabalhou = false;
            }

            if (!trabalhou)
                await Task.Delay(_options.IdleDelay, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }

        logger.LogInformation("Worker {WorkerId} encerrado", _options.WorkerId);
    }

    private async Task<bool> ProcessarProximoAsync(CancellationToken stoppingToken)
    {
        using var escopo = scopeFactory.CreateScope();

        var fila = escopo.ServiceProvider.GetRequiredService<IJobQueue>();
        var executores = escopo.ServiceProvider.GetServices<IJobHandler>().ToDictionary(h => h.Kind);

        if (executores.Count == 0)
            return false;

        var job = await fila.DequeueAsync(_options.WorkerId, [.. executores.Keys], _options.Lease, stoppingToken);
        if (job is null)
            return false;

        logger.LogInformation("Executando {Tipo} para {Alvo} (tentativa {Tentativa})", job.Kind, job.TargetId, job.Attempts);

        using var renovacao = new CancellationTokenSource();
        var renovando = RenovarReservaAsync(fila, job.Id, renovacao.Token);

        try
        {
            await executores[job.Kind].HandleAsync(job, stoppingToken);
            await fila.CompleteAsync(job.Id, CancellationToken.None);

            logger.LogInformation("Trabalho {JobId} concluído", job.Id);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Trabalho {JobId} falhou", job.Id);
            await fila.FailAsync(job.Id, e.Message, CancellationToken.None);
        }
        finally
        {
            await renovacao.CancelAsync();
            await renovando;
        }

        return true;
    }

    /// <summary>
    /// Mantém a reserva viva enquanto o trabalho executa. Sem isso, uma transcodificação
    /// longa perderia a reserva e outro worker começaria o mesmo trabalho em paralelo.
    /// </summary>
    private async Task RenovarReservaAsync(IJobQueue fila, Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_options.RenewInterval, cancellationToken);
                await fila.RenewAsync(jobId, _options.WorkerId, _options.Lease, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento normal: o trabalho terminou antes da próxima renovação.
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Não foi possível renovar a reserva do trabalho {JobId}", jobId);
        }
    }
}
