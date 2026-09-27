// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Entities;

namespace OpenTube.Domain.Tests.Entities;

public class AuthAttemptTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Registra_o_balde_e_o_instante()
    {
        var tentativa = AuthAttempt.Record("email:allan@barcelos.dev", Agora);

        Assert.Equal("email:allan@barcelos.dev", tentativa.Scope);
        Assert.Equal(Agora, tentativa.OccurredAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Exige_um_balde(string escopo)
    {
        Assert.ThrowsAny<ArgumentException>(() => AuthAttempt.Record(escopo, Agora));
    }
}
