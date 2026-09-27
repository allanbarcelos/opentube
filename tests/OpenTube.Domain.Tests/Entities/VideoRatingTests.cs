// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Entities;

namespace OpenTube.Domain.Tests.Entities;

public class VideoRatingTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Aceita_de_1_a_5(int nota) =>
        Assert.Equal(nota, VideoRating.Rate(Guid.NewGuid(), Guid.NewGuid(), nota, Agora).Score);

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void Recusa_fora_da_escala(int nota) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoRating.Rate(Guid.NewGuid(), Guid.NewGuid(), nota, Agora));

    [Fact]
    public void Trocar_a_nota_guarda_quando_mudou()
    {
        var avaliacao = VideoRating.Rate(Guid.NewGuid(), Guid.NewGuid(), 2, Agora);

        avaliacao.Change(4, Agora.AddDays(1));

        Assert.Equal(4, avaliacao.Score);
        Assert.Equal(Agora, avaliacao.CreatedAt);
        Assert.Equal(Agora.AddDays(1), avaliacao.UpdatedAt);
        Assert.Throws<ArgumentOutOfRangeException>(() => avaliacao.Change(9, Agora));
    }
}
