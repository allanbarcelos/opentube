// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Entities;

/// <summary>
/// Uma fatia da curva de retenção de um vídeo, já agregada. O painel lê daqui em vez de
/// recalcular a curva a partir dos trechos a cada abertura.
/// </summary>
public class VideoRetentionBucket
{
    private VideoRetentionBucket() { }

    public Guid VideoId { get; private set; }

    /// <summary>Índice da fatia, de 0 a 99.</summary>
    public int BucketIndex { get; private set; }

    /// <summary>Quantos espectadores distintos assistiram a esta fatia.</summary>
    public int Viewers { get; private set; }

    public static VideoRetentionBucket Create(Guid videoId, int bucketIndex, int viewers) => new()
    {
        VideoId = videoId,
        BucketIndex = bucketIndex,
        Viewers = viewers
    };

    public void Update(int viewers) => Viewers = viewers;
}
