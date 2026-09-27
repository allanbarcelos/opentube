// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTube.Infrastructure.Transcription;
using OpenTube.Worker.Media;

namespace OpenTube.Worker.Jobs;

/// <summary>
/// Informa, de tempos em tempos, se este worker consegue transcrever. É o que a aplicação
/// consulta para decidir se oferece a legenda automática: o Whisper pode demorar a subir
/// (baixando o modelo), cair ou nem existir na instalação.
/// </summary>
public class TranscriptionHeartbeat(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> options,
    ILogger<TranscriptionHeartbeat> logger) : BackgroundService
{
    /// <summary>
    /// Bem abaixo da validade do registro (<see cref="Domain.Entities.TranscriptionWorker.Validity"/>):
    /// uma verificação perdida não pode apagar o botão da tela.
    /// </summary>
    public static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(30);

    private readonly WorkerOptions _options = options.Value;

    private bool? _ultimo;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var relogio = new PeriodicTimer(Intervalo);

        try
        {
            do
            {
                try
                {
                    await InformarAsync(stoppingToken);
                }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(e, "Não foi possível informar a situação da transcrição");
                }
            }
            while (await relogio.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Encerramento do worker.
        }
    }

    public async Task InformarAsync(CancellationToken cancellationToken)
    {
        using var escopo = scopeFactory.CreateScope();

        var transcritor = escopo.ServiceProvider.GetRequiredService<ITranscriber>();
        var disponibilidade = escopo.ServiceProvider.GetRequiredService<TranscriptionAvailability>();

        var situacao = await transcritor.CheckAsync(cancellationToken);

        // Worker sem transcrição configurada e que nunca a teve não precisa ocupar o registro.
        if (!situacao.Available && !transcritor.IsAvailable && _ultimo is null)
        {
            _ultimo = false;
            return;
        }

        await disponibilidade.ReportAsync(_options.WorkerId, situacao.Available, situacao.Engine, cancellationToken);

        if (_ultimo != situacao.Available)
        {
            if (situacao.Available)
                logger.LogInformation("Transcrição automática disponível: {Motor}", situacao.Engine);
            else
                logger.LogWarning("Transcrição automática indisponível: o Whisper não respondeu");
        }

        _ultimo = situacao.Available;
    }
}
