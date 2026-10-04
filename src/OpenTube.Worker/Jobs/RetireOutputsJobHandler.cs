// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Queue;
using OpenTube.Infrastructure.Services;
using OpenTube.Infrastructure.Storage;

namespace OpenTube.Worker.Jobs;

/// <summary>
/// Apaga as saídas que deixaram de ser a geração publicada. Só age se o prefixo do trabalho
/// ainda for o que o vídeo aponta: uma geração mais nova, ou uma que nunca chegou a ser
/// publicada, não pode ser a régua do que fica.
/// </summary>
public class RetireOutputsJobHandler(
    OpenTubeDbContext db,
    IStorageReader storageReader,
    IStorageWriter storageWriter,
    ILogger<RetireOutputsJobHandler> logger) : IJobHandler
{
    public JobKind Kind => JobKind.RetireOutputs;

    public async Task HandleAsync(QueuedJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var payload = job.PayloadAs<RetireOutputsPayload>()
            ?? throw new InvalidOperationException("Parâmetros da limpeza de saídas ausentes.");

        // Atualizar a coluna para o próprio valor só conta as linhas que ainda apontam para
        // este prefixo. Se outra geração assumiu o lugar, a limpeza não corre.
        var aindaPublicada = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE videos SET hls_prefix = hls_prefix WHERE id = {payload.VideoId} AND hls_prefix = {payload.Prefix}",
            cancellationToken);

        if (aindaPublicada != 1)
        {
            logger.LogInformation("Geração {Prefixo} não é mais a publicada; limpeza ignorada", payload.Prefix);
            return;
        }

        var legendas = StorageKeys.CaptionsPrefix(payload.VideoId);
        var chaves = await storageReader.ListAsync(StorageBucket.Vod, StorageKeys.VodPrefix(payload.VideoId), cancellationToken);

        var antigas = chaves
            .Where(k => !k.StartsWith(payload.Prefix, StringComparison.Ordinal) && !k.StartsWith(legendas, StringComparison.Ordinal))
            .ToList();

        if (antigas.Count == 0)
            return;

        await storageWriter.DeleteKeysAsync(StorageBucket.Vod, antigas, cancellationToken);
        logger.LogInformation("Apagadas {Quantidade} saídas antigas de {VideoId}", antigas.Count, payload.VideoId);
    }
}
