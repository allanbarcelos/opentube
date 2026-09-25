using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Queue;

namespace OpenTube.Worker.Jobs;

/// <summary>
/// Coloca a manutenção do analytics na fila, se ainda não houver uma agendada. O trabalho se
/// reagenda sozinho depois; isto existe para a primeira partida e para o caso de a fila ter
/// sido esvaziada.
/// </summary>
public class AnalyticsBootstrapper(IServiceScopeFactory scopeFactory, ILogger<AnalyticsBootstrapper> logger)
    : BackgroundService
{
    private static readonly TimeSpan EsperaEntreTentativas = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Quantas vezes tentar antes de desistir. O worker costuma subir junto com a aplicação,
    /// que é quem aplica as migrações, então na primeira partida o esquema pode não existir
    /// ainda — desistir na primeira tentativa deixaria o analytics parado para sempre.
    /// </summary>
    private const int MaxTentativas = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var tentativa = 1; tentativa <= MaxTentativas && !stoppingToken.IsCancellationRequested; tentativa++)
        {
            try
            {
                if (await AgendarAsync(stoppingToken))
                    return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogDebug(e, "Esquema ainda não disponível para agendar a manutenção do analytics");
            }

            try
            {
                await Task.Delay(EsperaEntreTentativas, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (!stoppingToken.IsCancellationRequested)
            logger.LogError("Não foi possível agendar a manutenção do analytics após {Tentativas} tentativas", MaxTentativas);
    }

    /// <summary>Devolve <c>true</c> quando a manutenção já está garantida na fila.</summary>
    private async Task<bool> AgendarAsync(CancellationToken cancellationToken)
    {
        using var escopo = scopeFactory.CreateScope();

        var db = escopo.ServiceProvider.GetRequiredService<OpenTubeDbContext>();

        var jaAgendado = await db.ProcessingJobs.AnyAsync(
            j => j.Kind == JobKind.AnalyticsRollup &&
                 (j.Status == JobStatus.Pending || j.Status == JobStatus.Running),
            cancellationToken);

        if (jaAgendado)
            return true;

        await escopo.ServiceProvider.GetRequiredService<IJobQueue>()
            .EnqueueAsync(JobKind.AnalyticsRollup, cancellationToken: cancellationToken);

        logger.LogInformation("Manutenção do analytics agendada");

        return true;
    }
}
