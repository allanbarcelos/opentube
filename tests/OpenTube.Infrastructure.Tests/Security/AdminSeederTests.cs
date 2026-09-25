using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Entities;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Security;
using OpenTube.Infrastructure.Tests.Support;

namespace OpenTube.Infrastructure.Tests.Security;

[Collection(IntegrationCollection.Name)]
public class AdminSeederTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _relogio = new(Agora);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private AdminSeeder Criar(OpenTube.Infrastructure.Persistence.OpenTubeDbContext db, params string[] emails) =>
        new(db,
            Microsoft.Extensions.Options.Options.Create(new SecurityOptions
            {
                TokenPepper = "x",
                IpHashPepper = "y",
                AdminEmails = emails
            }),
            _relogio,
            NullLogger<AdminSeeder>.Instance);

    [Fact]
    public async Task Cria_os_administradores_configurados()
    {
        await using var db = postgres.CreateContext();

        var criados = await Criar(db, "allan@barcelos.dev", "outro@barcelos.dev").EnsureAdminsAsync();

        Assert.Equal(2, criados);
        Assert.Equal(2, await db.Users.CountAsync(u => u.IsAdmin));
    }

    [Fact]
    public async Task Rodar_duas_vezes_nao_duplica_ninguem()
    {
        await using var db = postgres.CreateContext();

        await Criar(db, "allan@barcelos.dev").EnsureAdminsAsync();
        var segunda = await Criar(db, "allan@barcelos.dev").EnsureAdminsAsync();

        Assert.Equal(0, segunda);
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Promove_quem_ja_existia_como_convidado()
    {
        await using var db = postgres.CreateContext();
        db.Users.Add(User.Create(EmailAddress.Parse("allan@barcelos.dev"), Agora));
        await db.SaveChangesAsync();

        await Criar(db, "allan@barcelos.dev").EnsureAdminsAsync();

        var usuario = await db.Users.SingleAsync();
        Assert.True(usuario.IsAdmin);
    }

    [Fact]
    public async Task Reativa_administrador_que_estava_desativado()
    {
        await using var db = postgres.CreateContext();
        var usuario = User.Create(EmailAddress.Parse("allan@barcelos.dev"), Agora);
        usuario.Disable(Agora);
        db.Users.Add(usuario);
        await db.SaveChangesAsync();

        await Criar(db, "allan@barcelos.dev").EnsureAdminsAsync();

        var lido = await db.Users.SingleAsync();
        Assert.True(lido.IsAdmin);
        Assert.True(lido.IsActive);
    }

    [Fact]
    public async Task Normaliza_a_caixa_e_ignora_repeticoes()
    {
        await using var db = postgres.CreateContext();

        await Criar(db, "Allan@Barcelos.dev", "allan@barcelos.dev", "  ALLAN@BARCELOS.DEV  ").EnsureAdminsAsync();

        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal("allan@barcelos.dev", (await db.Users.SingleAsync()).Email);
    }

    [Fact]
    public async Task Ignora_enderecos_malformados()
    {
        await using var db = postgres.CreateContext();

        var criados = await Criar(db, "nao-e-email", "allan@barcelos.dev").EnsureAdminsAsync();

        Assert.Equal(1, criados);
    }

    [Fact]
    public async Task Sem_configuracao_nenhum_administrador_e_criado()
    {
        await using var db = postgres.CreateContext();

        Assert.Equal(0, await Criar(db).EnsureAdminsAsync());
        Assert.Empty(await db.Users.ToListAsync());
    }
}
