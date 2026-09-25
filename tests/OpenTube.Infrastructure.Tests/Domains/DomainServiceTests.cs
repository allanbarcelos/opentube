using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Domains;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Security;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Domains;

[Collection(IntegrationCollection.Name)]
public class DomainServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();

    private readonly FakeTimeProvider _relogio = new(Agora);
    private readonly FakeDnsTxtLookup _dns = new();
    private readonly FakeEmailSender _emails = new();

    private readonly SecurityOptions _seguranca = new()
    {
        TokenPepper = "segredo",
        IpHashPepper = "segredo-ip",
        PublicUrl = "https://opentube.org",
        CodesPerHourPerIp = 5,
        CodesPerDayPerEmail = 10,
        CodesPerDayPerDomain = 100
    };

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private (DomainService Servico, OpenTubeDbContext Db) Criar()
    {
        var db = postgres.CreateContext();
        var opcoes = Microsoft.Extensions.Options.Options.Create(_seguranca);

        var auth = new PasswordlessAuthService(
            db, _emails, new AuthRateLimiter(db, opcoes, _relogio), new PrivacyHasher(opcoes),
            opcoes, _relogio, NullLogger<PasswordlessAuthService>.Instance);

        return (new DomainService(db, _dns, auth, _emails, opcoes, _relogio, NullLogger<DomainService>.Instance), db);
    }

    [Fact]
    public async Task Cadastra_o_dominio_sem_verificacao_e_indica_o_registro()
    {
        var (servico, db) = Criar();
        await using var _ = db;

        var dominio = await servico.RegisterAsync("  BARCELOS.DEV ", Admin, "ti@barcelos.dev");

        Assert.Equal("barcelos.dev", dominio.Name);
        Assert.False(dominio.IsVerified);
        Assert.Equal("_opentube-verify.barcelos.dev", dominio.VerificationRecordName);
        Assert.StartsWith("opentube-verify=", dominio.ExpectedRecordValue);
    }

    [Fact]
    public async Task Nao_cadastra_o_mesmo_dominio_duas_vezes()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        await servico.RegisterAsync("barcelos.dev", Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(() => servico.RegisterAsync("BARCELOS.DEV", Admin));
    }

    [Fact]
    public async Task Recusa_nome_que_nao_e_dominio()
    {
        var (servico, db) = Criar();
        await using var _ = db;

        await Assert.ThrowsAsync<ArgumentException>(() => servico.RegisterAsync("nao-e-dominio", Admin));
    }

    [Fact]
    public async Task Verifica_quando_o_registro_esta_publicado()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var dominio = await servico.RegisterAsync("barcelos.dev", Admin);
        _dns.Publicar(dominio.VerificationRecordName, dominio.ExpectedRecordValue);

        var resultado = await servico.VerifyAsync(dominio.Id);

        Assert.True(resultado.Verified);

        await using var leitura = postgres.CreateContext();
        Assert.True((await leitura.VerifiedDomains.SingleAsync()).IsVerified);
    }

    [Fact]
    public async Task Consulta_exatamente_o_registro_esperado()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var dominio = await servico.RegisterAsync("barcelos.dev", Admin);

        await servico.VerifyAsync(dominio.Id);

        Assert.Equal(["_opentube-verify.barcelos.dev"], _dns.Consultas);
    }

    [Fact]
    public async Task Nao_verifica_sem_o_registro_e_mostra_o_que_havia()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var dominio = await servico.RegisterAsync("barcelos.dev", Admin);
        _dns.Publicar(dominio.VerificationRecordName, "v=spf1 include:exemplo.com ~all");

        var resultado = await servico.VerifyAsync(dominio.Id);

        Assert.False(resultado.Verified);
        Assert.Contains("v=spf1 include:exemplo.com ~all", resultado.FoundRecords);
    }

    [Fact]
    public async Task Nao_verifica_com_o_token_de_outro_dominio()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var meu = await servico.RegisterAsync("barcelos.dev", Admin);
        var alheio = await servico.RegisterAsync("outra.com", Admin);

        // Publicar o token do vizinho não pode servir para comprovar o meu domínio.
        _dns.Publicar(meu.VerificationRecordName, alheio.ExpectedRecordValue);

        Assert.False((await servico.VerifyAsync(meu.Id)).Verified);
    }

    [Fact]
    public async Task Reemitir_o_token_derruba_a_verificacao()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var dominio = await servico.RegisterAsync("barcelos.dev", Admin);
        _dns.Publicar(dominio.VerificationRecordName, dominio.ExpectedRecordValue);
        await servico.VerifyAsync(dominio.Id);

        var tokenAnterior = dominio.VerificationToken;

        var reemitido = await servico.ResetVerificationAsync(dominio.Id);

        Assert.False(reemitido.IsVerified);
        Assert.NotEqual(tokenAnterior, reemitido.VerificationToken);
        Assert.False((await servico.VerifyAsync(dominio.Id)).Verified);
    }

    [Fact]
    public async Task A_porta_de_entrada_so_abre_depois_de_verificar()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var dominio = await servico.RegisterAsync("barcelos.dev", Admin);

        Assert.Null(await servico.FindOpenEntryAsync("barcelos.dev"));

        _dns.Publicar(dominio.VerificationRecordName, dominio.ExpectedRecordValue);
        await servico.VerifyAsync(dominio.Id);

        Assert.NotNull(await servico.FindOpenEntryAsync("BARCELOS.DEV"));
    }

    [Fact]
    public async Task O_endereco_da_porta_pode_ser_trocado_por_um_nao_adivinhavel()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var dominio = await VerificadoAsync(servico, "barcelos.dev");

        await servico.UpdateAsync(dominio.Id, "x7k2-privado", true, null, null, null);

        Assert.Null(await servico.FindOpenEntryAsync("barcelos.dev"));
        Assert.NotNull(await servico.FindOpenEntryAsync("x7k2-privado"));
        Assert.Equal("https://opentube.org/entry/x7k2-privado", servico.EntryUrl((await servico.FindAsync(dominio.Id))!));
    }

    [Fact]
    public async Task Duas_portas_nao_podem_ter_o_mesmo_endereco()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        await VerificadoAsync(servico, "barcelos.dev");
        var segundo = await VerificadoAsync(servico, "outra.com");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servico.UpdateAsync(segundo.Id, "barcelos.dev", true, null, null, null));
    }

    [Fact]
    public async Task Desligar_a_porta_fecha_a_entrada_sem_perder_a_verificacao()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var dominio = await VerificadoAsync(servico, "barcelos.dev");

        await servico.UpdateAsync(dominio.Id, null, entryEnabled: false, null, null, null);

        Assert.Null(await servico.FindOpenEntryAsync("barcelos.dev"));
        Assert.True((await servico.FindAsync(dominio.Id))!.IsVerified);
    }

    [Fact]
    public async Task O_pedido_de_codigo_exige_que_o_email_seja_do_dominio()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var dominio = await VerificadoAsync(servico, "barcelos.dev");
        await ConcederAoDominioAsync(dominio.Name);

        Assert.Equal(DomainEntryFailure.EmailNotAccepted,
            await servico.RequestEntryCodeAsync("barcelos.dev", "alguem@outra.com"));
        Assert.Empty(_emails.Sent);
    }

    [Fact]
    public async Task Envia_o_codigo_para_quem_e_do_dominio_e_tem_acesso()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var dominio = await VerificadoAsync(servico, "barcelos.dev");
        await ConcederAoDominioAsync(dominio.Name);

        var resultado = await servico.RequestEntryCodeAsync("barcelos.dev", "allan@barcelos.dev");

        Assert.Equal(DomainEntryFailure.None, resultado);
        Assert.Single(_emails.Sent);
    }

    [Fact]
    public async Task Sem_concessao_ao_dominio_nenhum_email_sai()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        await VerificadoAsync(servico, "barcelos.dev");

        var resultado = await servico.RequestEntryCodeAsync("barcelos.dev", "allan@barcelos.dev");

        // A resposta é de sucesso para não revelar quem tem acesso, mas nada é enviado.
        Assert.Equal(DomainEntryFailure.None, resultado);
        Assert.Empty(_emails.Sent);
    }

    [Fact]
    public async Task A_lista_de_permitidos_restringe_quem_entra()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var dominio = await VerificadoAsync(servico, "barcelos.dev");
        await ConcederAoDominioAsync(dominio.Name);

        await servico.UpdateAsync(dominio.Id, null, true, ["allan@barcelos.dev"], null, null);

        Assert.Equal(DomainEntryFailure.None,
            await servico.RequestEntryCodeAsync("barcelos.dev", "allan@barcelos.dev"));
        Assert.Equal(DomainEntryFailure.EmailNotAccepted,
            await servico.RequestEntryCodeAsync("barcelos.dev", "outro@barcelos.dev"));
    }

    [Fact]
    public async Task Porta_inexistente_nao_revela_nada()
    {
        var (servico, db) = Criar();
        await using var _ = db;

        Assert.Equal(DomainEntryFailure.DomainNotFound,
            await servico.RequestEntryCodeAsync("nao-existe.com", "alguem@nao-existe.com"));
    }

    [Fact]
    public async Task Endereco_malformado_e_recusado()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        await VerificadoAsync(servico, "barcelos.dev");

        Assert.Equal(DomainEntryFailure.InvalidEmail,
            await servico.RequestEntryCodeAsync("barcelos.dev", "nao-e-email"));
    }

    [Fact]
    public async Task O_limite_de_taxa_vale_tambem_na_porta_do_dominio()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var dominio = await VerificadoAsync(servico, "barcelos.dev");
        await ConcederAoDominioAsync(dominio.Name);

        for (var i = 0; i < _seguranca.CodesPerHourPerIp; i++)
            await servico.RequestEntryCodeAsync("barcelos.dev", "allan@barcelos.dev", "203.0.113.5");

        Assert.Equal(DomainEntryFailure.RateLimited,
            await servico.RequestEntryCodeAsync("barcelos.dev", "allan@barcelos.dev", "203.0.113.5"));
    }

    [Fact]
    public async Task Envia_ao_responsavel_o_endereco_da_porta()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var dominio = await VerificadoAsync(servico, "barcelos.dev", "ti@barcelos.dev");

        Assert.True(await servico.SendEntryLinkAsync(dominio.Id));

        var mensagem = _emails.Last!;
        Assert.Equal("ti@barcelos.dev", mensagem.To);
        Assert.Contains("https://opentube.org/entry/barcelos.dev", mensagem.TextBody);
        Assert.Contains("receives an access code", mensagem.TextBody);
    }

    [Fact]
    public async Task Sem_responsavel_nada_e_enviado()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var dominio = await VerificadoAsync(servico, "barcelos.dev");

        Assert.False(await servico.SendEntryLinkAsync(dominio.Id));
        Assert.Empty(_emails.Sent);
    }

    [Fact]
    public async Task Recusa_operar_sobre_dominio_inexistente()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        var inexistente = Guid.CreateVersion7();

        await Assert.ThrowsAsync<InvalidOperationException>(() => servico.VerifyAsync(inexistente));
        await Assert.ThrowsAsync<InvalidOperationException>(() => servico.ResetVerificationAsync(inexistente));
    }

    private async Task<VerifiedDomain> VerificadoAsync(DomainService servico, string nome, string? contato = null)
    {
        var dominio = await servico.RegisterAsync(nome, Admin, contato);

        _dns.Publicar(dominio.VerificationRecordName, dominio.ExpectedRecordValue);
        await servico.VerifyAsync(dominio.Id);

        return (await servico.FindAsync(dominio.Id))!;
    }

    private async Task ConcederAoDominioAsync(string nome)
    {
        await using var db = postgres.CreateContext();

        db.AccessGrants.Add(AccessGrant.ForDomain(nome, GrantTargetType.All, null, Admin, Agora));
        await db.SaveChangesAsync();
    }
}
