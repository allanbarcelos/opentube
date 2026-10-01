// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

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

    /// <summary>
    /// Trabalhos que correm na própria fila. Uma transcrição leva de minutos a horas; na mesma
    /// fila das transcodificações, um vídeo enviado agora esperaria a legenda de outro terminar.
    /// </summary>
    public static readonly IReadOnlySet<JobKind> Transcricao = new HashSet<JobKind> { JobKind.Transcript };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker {WorkerId} iniciado", _options.WorkerId);

        await Task.WhenAll(
            ConsumirAsync(tipo => !Transcricao.Contains(tipo), stoppingToken),
            ConsumirAsync(Transcricao.Contains, stoppingToken));

        logger.LogInformation("Worker {WorkerId} encerrado", _options.WorkerId);
    }

    private async Task ConsumirAsync(Func<JobKind, bool> atende, CancellationToken stoppingToken)
    {
        // Cede a thread de quem chamou: os dois laços começam juntos.
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            bool trabalhou;

            try
            {
                trabalhou = await ProcessarProximoAsync(atende, stoppingToken);
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
    }

    private async Task<bool> ProcessarProximoAsync(Func<JobKind, bool> atende, CancellationToken stoppingToken)
    {
        using var escopo = scopeFactory.CreateScope();

        var fila = escopo.ServiceProvider.GetRequiredService<IJobQueue>();
        var executores = escopo.ServiceProvider.GetServices<IJobHandler>()
            .Where(h => atende(h.Kind))
            .ToDictionary(h => h.Kind);

        if (executores.Count == 0)
            return false;

        var job = await fila.DequeueAsync(_options.WorkerId, [.. executores.Keys], _options.Lease, stoppingToken);
        if (job is null)
            return false;

        logger.LogInformation("Executando {Tipo} para {Alvo} (tentativa {Tentativa})", job.Kind, job.TargetId, job.Attempts);

        using var renovacao = new CancellationTokenSource();
        using var reservaPerdida = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renovando = RenovarReservaAsync(job.Id, reservaPerdida, renovacao.Token);

        try
        {
            await executores[job.Kind].HandleAsync(job, reservaPerdida.Token);
            await fila.CompleteAsync(job.Id, CancellationToken.None);

            logger.LogInformation("Trabalho {JobId} concluído", job.Id);
        }
        catch (OperationCanceledException) when (reservaPerdida.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            // A reserva passou para outro worker: marcar falha ou conclusão agora mexeria no
            // trabalho que ele está executando.
            logger.LogWarning("Trabalho {JobId} abandonado: a reserva foi perdida", job.Id);
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
    /// longa perderia a reserva e outro worker começaria o mesmo trabalho em paralelo. Cada
    /// renovação usa um escopo próprio: o contexto do banco do trabalho não aceita duas
    /// operações ao mesmo tempo, e o executor o usa enquanto a renovação corre. Se a reserva
    /// já for de outro worker, o trabalho é cancelado em vez de seguir em paralelo.
    /// </summary>
    private async Task RenovarReservaAsync(Guid jobId, CancellationTokenSource reservaPerdida, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.RenewInterval, cancellationToken);

                using var escopo = scopeFactory.CreateScope();
                var fila = escopo.ServiceProvider.GetRequiredService<IJobQueue>();

                if (!await fila.RenewAsync(jobId, _options.WorkerId, _options.Lease, cancellationToken))
                {
                    logger.LogWarning("A reserva do trabalho {JobId} não é mais deste worker", jobId);
                    await reservaPerdida.CancelAsync();
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Encerramento normal: o trabalho terminou antes da próxima renovação.
                return;
            }
            catch (Exception e)
            {
                // Uma falha momentânea do banco não encerra a renovação: a próxima volta tenta
                // de novo, ainda dentro do prazo da reserva.
                logger.LogWarning(e, "Não foi possível renovar a reserva do trabalho {JobId}", jobId);
            }
        }
    }
}
