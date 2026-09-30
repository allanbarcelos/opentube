// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Security;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Security;

[Collection(IntegrationCollection.Name)]
public class AuthRateLimiterTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly EmailAddress Email = EmailAddress.Parse("allan@barcelos.dev");

    private readonly FakeTimeProvider _relogio = new(Agora);

    private readonly SecurityOptions _opcoes = new()
    {
        TokenPepper = "x",
        IpHashPepper = "y",
        CodesPerWindow = 3
    };

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private AuthRateLimiter Criar(OpenTube.Infrastructure.Persistence.OpenTubeDbContext db) =>
        new(db, Microsoft.Extensions.Options.Options.Create(_opcoes), _relogio);

    [Fact]
    public async Task Libera_enquanto_esta_dentro_dos_limites()
    {
        await using var db = postgres.CreateContext();
        var limitador = Criar(db);

        Assert.True((await limitador.CheckAsync(Email, "ip-hash")).Allowed);
    }

    [Fact]
    public async Task Barra_o_mesmo_email_dentro_da_janela_e_diz_quanto_falta()
    {
        await using var db = postgres.CreateContext();
        var limitador = Criar(db);

        for (var i = 0; i < _opcoes.CodesPerWindow; i++)
            await limitador.RecordAsync(Email, $"ip-{i}");

        _relogio.Advance(TimeSpan.FromMinutes(4));
        var resultado = await limitador.CheckAsync(Email, "ip-novo");

        Assert.False(resultado.Allowed);
        Assert.Equal("email", resultado.Scope);
        Assert.Equal(AuthRateLimiter.Janela - TimeSpan.FromMinutes(4), resultado.RetryAfter);
    }

    [Fact]
    public async Task Outro_email_nao_herda_o_bloqueio()
    {
        await using var db = postgres.CreateContext();
        var limitador = Criar(db);

        for (var i = 0; i < _opcoes.CodesPerWindow; i++)
            await limitador.RecordAsync(Email, "ip-hash");

        Assert.True((await limitador.CheckAsync(EmailAddress.Parse("outra@barcelos.dev"), "ip-hash")).Allowed);
    }

    [Fact]
    public async Task A_janela_se_solta_em_dez_minutos()
    {
        await using var db = postgres.CreateContext();
        var limitador = Criar(db);

        for (var i = 0; i < _opcoes.CodesPerWindow; i++)
            await limitador.RecordAsync(Email, null);

        _relogio.Advance(AuthRateLimiter.Janela + TimeSpan.FromSeconds(1));

        Assert.True((await limitador.CheckAsync(Email, null)).Allowed);
    }

    [Fact]
    public async Task Zero_nao_barra_o_primeiro_pedido()
    {
        _opcoes.CodesPerWindow = 0;
        await using var db = postgres.CreateContext();
        var limitador = Criar(db);

        Assert.True((await limitador.CheckAsync(Email, null)).Allowed);
    }

    [Fact]
    public async Task A_limpeza_descarta_apenas_o_que_saiu_da_janela()
    {
        await using var db = postgres.CreateContext();
        var limitador = Criar(db);

        await limitador.RecordAsync(Email, null);
        _relogio.Advance(AuthRateLimiter.Janela + TimeSpan.FromMinutes(1));
        await limitador.RecordAsync(Email, null);

        var descartados = await limitador.PruneAsync();

        Assert.Equal(1, descartados);
        Assert.Equal(1, await db.AuthAttempts.CountAsync());
    }
}
