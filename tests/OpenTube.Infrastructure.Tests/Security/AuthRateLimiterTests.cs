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
        CodesPerHourPerIp = 3,
        CodesPerDayPerEmail = 5,
        CodesPerDayPerDomain = 7
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
    public async Task Barra_ao_estourar_o_limite_por_origem()
    {
        await using var db = postgres.CreateContext();
        var limitador = Criar(db);

        for (var i = 0; i < _opcoes.CodesPerHourPerIp; i++)
            await limitador.RecordAsync(Email, "ip-hash");

        var resultado = await limitador.CheckAsync(Email, "ip-hash");

        Assert.False(resultado.Allowed);
        Assert.Equal("origem", resultado.Scope);
        Assert.Equal(TimeSpan.FromHours(1), resultado.RetryAfter);
    }

    [Fact]
    public async Task Outra_origem_nao_herda_o_bloqueio()
    {
        await using var db = postgres.CreateContext();
        var limitador = Criar(db);

        for (var i = 0; i < _opcoes.CodesPerHourPerIp; i++)
            await limitador.RecordAsync(EmailAddress.Parse($"pessoa{i}@outrodominio.dev"), "ip-bloqueado");

        Assert.True((await limitador.CheckAsync(Email, "ip-livre")).Allowed);
    }

    [Fact]
    public async Task Barra_ao_estourar_o_limite_por_email_mesmo_trocando_de_origem()
    {
        await using var db = postgres.CreateContext();
        var limitador = Criar(db);

        for (var i = 0; i < _opcoes.CodesPerDayPerEmail; i++)
            await limitador.RecordAsync(Email, $"ip-{i}");

        var resultado = await limitador.CheckAsync(Email, "ip-novo");

        Assert.False(resultado.Allowed);
        Assert.Equal("email", resultado.Scope);
    }

    [Fact]
    public async Task Barra_ao_estourar_o_limite_por_dominio()
    {
        await using var db = postgres.CreateContext();
        var limitador = Criar(db);

        for (var i = 0; i < _opcoes.CodesPerDayPerDomain; i++)
            await limitador.RecordAsync(EmailAddress.Parse($"pessoa{i}@barcelos.dev"), $"ip-{i}");

        var resultado = await limitador.CheckAsync(Email, "ip-novo");

        Assert.False(resultado.Allowed);
        Assert.Equal("domínio", resultado.Scope);
    }

    [Fact]
    public async Task Pedido_sem_origem_conhecida_ainda_conta_para_email_e_dominio()
    {
        await using var db = postgres.CreateContext();
        var limitador = Criar(db);

        for (var i = 0; i < _opcoes.CodesPerDayPerEmail; i++)
            await limitador.RecordAsync(Email, null);

        Assert.False((await limitador.CheckAsync(Email, null)).Allowed);
    }

    [Fact]
    public async Task A_janela_por_origem_se_solta_em_uma_hora()
    {
        await using var db = postgres.CreateContext();
        var limitador = Criar(db);

        for (var i = 0; i < _opcoes.CodesPerHourPerIp; i++)
            await limitador.RecordAsync(Email, "ip-hash");

        _relogio.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));

        Assert.True((await limitador.CheckAsync(Email, "ip-hash")).Allowed);
    }

    [Fact]
    public async Task A_janela_por_email_se_solta_em_um_dia()
    {
        await using var db = postgres.CreateContext();
        var limitador = Criar(db);

        for (var i = 0; i < _opcoes.CodesPerDayPerEmail; i++)
            await limitador.RecordAsync(Email, $"ip-{i}");

        _relogio.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));

        Assert.True((await limitador.CheckAsync(Email, "ip-novo")).Allowed);
    }

    [Fact]
    public async Task A_limpeza_descarta_apenas_o_que_nao_influencia_mais()
    {
        await using var db = postgres.CreateContext();
        var limitador = Criar(db);

        await limitador.RecordAsync(Email, "ip-antigo");
        _relogio.Advance(TimeSpan.FromDays(2));
        await limitador.RecordAsync(Email, "ip-recente");

        var descartados = await limitador.PruneAsync();

        Assert.Equal(3, descartados);
        Assert.Equal(3, await db.AuthAttempts.CountAsync());
    }
}
