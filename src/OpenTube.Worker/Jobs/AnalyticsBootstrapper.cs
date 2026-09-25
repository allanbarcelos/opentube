using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Queue;

namespace OpenTube.Worker.Jobs;

/// <summary>
/// Coloca a manutenção do analytics na fila ao subir, se ainda não houver uma agendada. O
/// trabalho se reagenda sozinho depois; isto existe só para a primeira partida e para o caso
/// de a fila ter sido esvaziada.
/// </summary>
public class AnalyticsBootstrapper(IServiceScopeFactory scopeFactory, ILogger<AnalyticsBootstrapper> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var escopo = scopeFactory.CreateScope();

            var db = escopo.ServiceProvider.GetRequiredService<OpenTubeDbContext>();

            var jaAgendado = await db.ProcessingJobs.AnyAsync(
                j => j.Kind == JobKind.AnalyticsRollup &&
                     (j.Status == JobStatus.Pending || j.Status == JobStatus.Running),
                cancellationToken);

            if (jaAgendado)
                return;

            await escopo.ServiceProvider.GetRequiredService<IJobQueue>()
                .EnqueueAsync(JobKind.AnalyticsRollup, cancellationToken: cancellationToken);

            logger.LogInformation("Manutenção do analytics agendada");
        }
        catch (Exception e)
        {
            // Não impedir a subida do worker por causa disto: a transcodificação é o que
            // realmente não pode parar.
            logger.LogError(e, "Não foi possível agendar a manutenção do analytics");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
