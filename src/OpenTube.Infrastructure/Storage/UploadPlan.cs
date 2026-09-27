// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Infrastructure.Storage;

/// <summary>
/// Divisão de um arquivo em pedaços para o envio multipart.
/// </summary>
/// <param name="PartSizeBytes">Tamanho de cada pedaço, exceto o último.</param>
/// <param name="PartCount">Quantidade total de pedaços.</param>
public readonly record struct UploadPlan(int PartSizeBytes, int PartCount)
{
    /// <summary>Menor pedaço aceito pelo protocolo S3, fora o último.</summary>
    public const int MinPartSize = 5 * 1024 * 1024;

    /// <summary>Maior quantidade de pedaços que um envio multipart admite.</summary>
    public const int MaxParts = 10_000;

    /// <summary>
    /// Calcula a divisão para um arquivo do tamanho informado. Pedaços maiores reduzem o
    /// número de requisições; o piso de 5 MiB é exigência do protocolo, e o teto de 10.000
    /// pedaços obriga a aumentar o pedaço conforme o arquivo cresce.
    /// </summary>
    public static UploadPlan For(long fileSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fileSizeBytes, 0);

        var partSize = (long)MinPartSize;

        if (fileSizeBytes > partSize * MaxParts)
        {
            // Arredonda para cima no múltiplo de 1 MiB seguinte, para manter números redondos.
            var necessario = (fileSizeBytes + MaxParts - 1) / MaxParts;
            const long umMiB = 1024 * 1024;
            partSize = (necessario + umMiB - 1) / umMiB * umMiB;
        }

        if (partSize > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(fileSizeBytes), "Arquivo grande demais para o envio multipart.");

        var partCount = (int)((fileSizeBytes + partSize - 1) / partSize);

        return new UploadPlan((int)partSize, partCount);
    }
}
