using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Access;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Access;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Security;
using OpenTube.Infrastructure.Storage;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Access;

[Collection(IntegrationCollection.Name)]
public class GrantServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();

    private readonly FakeTimeProvider _relogio = new(Agora);
    private readonly FakeEmailSender _emails = new();

    private readonly SecurityOptions _seguranca = new()
    {
        TokenPepper = "segredo-de-teste",
        IpHashPepper = "segredo-de-ip",
        InviteLifetime = TimeSpan.FromDays(7),
        SessionLifetime = TimeSpan.FromDays(30),
        PublicUrl = "https://opentube.org"
    };

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private (GrantService Servico, OpenTubeDbContext Db) Criar()
    {
        var db = postgres.CreateContext();
        var opcoes = Microsoft.Extensions.Options.Options.Create(_seguranca);

        var auth = new PasswordlessAuthService(
            db, _emails, new AuthRateLimiter(db, opcoes, _relogio), new PrivacyHasher(opcoes),
            opcoes, _relogio, NullLogger<PasswordlessAuthService>.Instance);

        return (new GrantService(db, auth, _emails, opcoes, _relogio, NullLogger<GrantService>.Instance), db);
    }

    private async Task<Video> VideoRestritoAsync(string titulo = "Plano Confidencial")
    {
        await using var db = postgres.CreateContext();
        var videoId = Guid.CreateVersion7();

        var video = Video.CreateDraft(titulo, $"v-{videoId:n}"[..20], "originals/a.mp4", Admin, Agora, id: videoId);
        video.MarkUploaded(1024);
        video.StartProcessing();
        video.MarkReady(StorageKeys.VodPrefix(videoId), 120, 1280, 720, null, null, Agora);
        video.ChangeVisibility(VideoVisibility.Restricted);

        db.Videos.Add(video);
        await db.SaveChangesAsync();

        return video;
    }

    private AccessService CriarAcesso(OpenTubeDbContext db) =>
        new(db, Microsoft.Extensions.Options.Options.Create(_seguranca), _relogio);

    [Fact]
    public async Task Convida_uma_pessoa_e_libera_o_video()
    {
        var video = await VideoRestritoAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        var resultados = await servico.InviteAsync(
            ["allan@barcelos.dev"], GrantTargetType.Video, video.Id, GrantValidity.Forever, Admin);

        var convite = Assert.Single(resultados);
        Assert.Equal("allan@barcelos.dev", convite.Email);
        Assert.True(convite.EmailSent);

        var espectador = Viewer.Authenticated(Guid.CreateVersion7(), EmailAddress.Parse("allan@barcelos.dev"));

        await using var leitura = postgres.CreateContext();
        Assert.True((await CriarAcesso(leitura).EvaluateAsync(espectador, video)).Allowed);
    }

    [Fact]
    public async Task O_convite_diz_o_que_foi_liberado_e_por_quanto_tempo()
    {
        var video = await VideoRestritoAsync("Reunião Trimestral");
        var (servico, db) = Criar();
        await using var _ = db;

        await servico.InviteAsync(
            ["allan@barcelos.dev"], GrantTargetType.Video, video.Id,
            GrantValidity.For(TimeSpan.FromDays(30)), Admin);

        var mensagem = _emails.Last!;

        Assert.Contains("Reunião Trimestral", mensagem.Subject);
        Assert.Contains("30 dias a partir do primeiro acesso", mensagem.TextBody);
        Assert.Contains("https://opentube.org/entrar/", mensagem.TextBody);
        Assert.Matches(@"\b\d{6}\b", mensagem.TextBody);
    }

    [Fact]
    public async Task Convida_varias_pessoas_de_uma_vez()
    {
        var video = await VideoRestritoAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        var resultados = await servico.InviteAsync(
            ["a@barcelos.dev", "b@barcelos.dev", "c@barcelos.dev"],
            GrantTargetType.Video, video.Id, GrantValidity.Forever, Admin);

        Assert.Equal(3, resultados.Count);
        Assert.Equal(3, _emails.Sent.Count);

        await using var leitura = postgres.CreateContext();
        Assert.Equal(3, await leitura.AccessGrants.CountAsync());
    }

    [Fact]
    public async Task Descarta_enderecos_repetidos_e_invalidos()
    {
        var video = await VideoRestritoAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        var resultados = await servico.InviteAsync(
            ["allan@barcelos.dev", "ALLAN@BARCELOS.DEV", "nao-e-email", "  "],
            GrantTargetType.Video, video.Id, GrantValidity.Forever, Admin);

        Assert.Single(resultados);
    }

    [Fact]
    public async Task Sem_nenhum_endereco_valido_o_convite_e_recusado()
    {
        var video = await VideoRestritoAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servico.InviteAsync(["nao-e-email"], GrantTargetType.Video, video.Id, GrantValidity.Forever, Admin));
    }

    [Fact]
    public async Task Reconvidar_a_mesma_pessoa_nao_cria_concessao_duplicada()
    {
        var video = await VideoRestritoAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        var primeiro = await servico.InviteAsync(
            ["allan@barcelos.dev"], GrantTargetType.Video, video.Id, GrantValidity.Forever, Admin);
        var segundo = await servico.InviteAsync(
            ["allan@barcelos.dev"], GrantTargetType.Video, video.Id, GrantValidity.Forever, Admin);

        Assert.Equal(primeiro[0].GrantId, segundo[0].GrantId);

        await using var leitura = postgres.CreateContext();
        Assert.Equal(1, await leitura.AccessGrants.CountAsync());
        // O email sai de novo: reenviar o convite é justamente o caso de uso.
        Assert.Equal(2, _emails.Sent.Count);
    }

    [Fact]
    public async Task Reconvidar_reativa_uma_concessao_revogada()
    {
        var video = await VideoRestritoAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        var convite = await servico.InviteAsync(
            ["allan@barcelos.dev"], GrantTargetType.Video, video.Id, GrantValidity.Forever, Admin);
        await servico.RevokeAsync(convite[0].GrantId);

        await servico.InviteAsync(
            ["allan@barcelos.dev"], GrantTargetType.Video, video.Id, GrantValidity.Forever, Admin);

        await using var leitura = postgres.CreateContext();
        Assert.False((await leitura.AccessGrants.SingleAsync()).IsRevoked);
    }

    [Fact]
    public async Task Convite_sem_email_apenas_concede_o_acesso()
    {
        var video = await VideoRestritoAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        var resultados = await servico.InviteAsync(
            ["allan@barcelos.dev"], GrantTargetType.Video, video.Id, GrantValidity.Forever, Admin, sendEmail: false);

        Assert.False(resultados[0].EmailSent);
        Assert.Empty(_emails.Sent);
    }

    [Fact]
    public async Task Concede_acesso_a_um_dominio_inteiro()
    {
        var video = await VideoRestritoAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        await servico.GrantToDomainAsync("  BARCELOS.DEV ", GrantTargetType.Video, video.Id, GrantValidity.Forever, Admin);

        await using var leitura = postgres.CreateContext();
        var concessao = await leitura.AccessGrants.SingleAsync();

        Assert.Equal("barcelos.dev", concessao.SubjectValue);

        var qualquerPessoa = Viewer.Authenticated(Guid.CreateVersion7(), EmailAddress.Parse("outra@barcelos.dev"));
        Assert.True((await CriarAcesso(leitura).EvaluateAsync(qualquerPessoa, video)).Allowed);
    }

    [Fact]
    public async Task Conceder_o_mesmo_dominio_duas_vezes_apenas_atualiza_a_validade()
    {
        var video = await VideoRestritoAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        await servico.GrantToDomainAsync("barcelos.dev", GrantTargetType.Video, video.Id, GrantValidity.Forever, Admin);
        await servico.GrantToDomainAsync("barcelos.dev", GrantTargetType.Video, video.Id,
            GrantValidity.Until(Agora.AddDays(30)), Admin);

        await using var leitura = postgres.CreateContext();
        var concessao = await leitura.AccessGrants.SingleAsync();

        Assert.Equal(Agora.AddDays(30), concessao.ExpiresAt);
    }

    [Fact]
    public async Task Cria_link_de_compartilhamento_que_da_acesso()
    {
        var video = await VideoRestritoAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        var link = await servico.CreateShareLinkAsync(
            GrantTargetType.Video, video.Id, GrantValidity.Forever, Admin);

        Assert.StartsWith("https://opentube.org/l/", link.Url);

        var token = link.Url[(link.Url.LastIndexOf('/') + 1)..];

        await using var leitura = postgres.CreateContext();
        var acesso = CriarAcesso(leitura);
        var espectador = await acesso.ResolveLinkAsync(Viewer.Anonymous, token);

        Assert.True((await acesso.EvaluateAsync(espectador, video)).Allowed);
    }

    [Fact]
    public async Task O_token_do_link_nao_fica_guardado()
    {
        var video = await VideoRestritoAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        var link = await servico.CreateShareLinkAsync(GrantTargetType.Video, video.Id, GrantValidity.Forever, Admin);
        var token = link.Url[(link.Url.LastIndexOf('/') + 1)..];

        await using var leitura = postgres.CreateContext();
        var guardado = await leitura.AccessGrants.Select(g => g.SubjectValue).SingleAsync();

        Assert.DoesNotContain(token, guardado);
    }

    [Fact]
    public async Task O_link_pode_ter_limite_de_visualizacoes()
    {
        var video = await VideoRestritoAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        var link = await servico.CreateShareLinkAsync(
            GrantTargetType.Video, video.Id, GrantValidity.Forever, Admin, maxViews: 5);

        await using var leitura = postgres.CreateContext();
        Assert.Equal(5, (await leitura.AccessGrants.SingleAsync()).MaxViews);
    }

    [Fact]
    public async Task Revogar_corta_o_acesso_e_restaurar_devolve()
    {
        var video = await VideoRestritoAsync();
        var (servico, db) = Criar();
        await using var _ = db;
        var convite = await servico.InviteAsync(
            ["allan@barcelos.dev"], GrantTargetType.Video, video.Id, GrantValidity.Forever, Admin);

        var espectador = Viewer.Authenticated(Guid.CreateVersion7(), EmailAddress.Parse("allan@barcelos.dev"));

        await servico.RevokeAsync(convite[0].GrantId);

        await using (var leitura = postgres.CreateContext())
            Assert.False((await CriarAcesso(leitura).EvaluateAsync(espectador, video)).Allowed);

        await servico.RestoreAsync(convite[0].GrantId);

        await using (var leitura = postgres.CreateContext())
            Assert.True((await CriarAcesso(leitura).EvaluateAsync(espectador, video)).Allowed);
    }

    [Fact]
    public async Task Revogar_concessao_inexistente_e_recusado()
    {
        var (servico, db) = Criar();
        await using var _ = db;

        await Assert.ThrowsAsync<InvalidOperationException>(() => servico.RevokeAsync(Guid.CreateVersion7()));
    }

    [Fact]
    public async Task Lista_as_concessoes_de_um_alvo_e_de_uma_pessoa()
    {
        var video = await VideoRestritoAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        await servico.InviteAsync(["a@barcelos.dev", "b@barcelos.dev"],
            GrantTargetType.Video, video.Id, GrantValidity.Forever, Admin);
        await servico.InviteAsync(["a@barcelos.dev"], GrantTargetType.All, null, GrantValidity.Forever, Admin);

        Assert.Equal(2, (await servico.ListForTargetAsync(GrantTargetType.Video, video.Id)).Count);
        Assert.Equal(2, (await servico.ListForEmailAsync("A@Barcelos.dev")).Count);
    }

    [Fact]
    public async Task O_convite_permite_entrar_pelo_link_recebido()
    {
        var video = await VideoRestritoAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        await servico.InviteAsync(["allan@barcelos.dev"], GrantTargetType.Video, video.Id, GrantValidity.Forever, Admin);

        var opcoes = Microsoft.Extensions.Options.Options.Create(_seguranca);
        var auth = new PasswordlessAuthService(
            db, _emails, new AuthRateLimiter(db, opcoes, _relogio), new PrivacyHasher(opcoes),
            opcoes, _relogio, NullLogger<PasswordlessAuthService>.Instance);

        var entrada = await auth.VerifyTokenAsync(_emails.LastToken());

        Assert.True(entrada.Succeeded);
        // O convidado passa a existir como usuário só agora, na primeira entrada.
        Assert.Equal("allan@barcelos.dev", entrada.User!.Email);
        Assert.False(entrada.User.IsAdmin);
    }

    [Theory]
    [InlineData(null, null, "sem prazo")]
    [InlineData(30, null, "30 dias a partir do primeiro acesso")]
    public void Descreve_a_validade_em_portugues(int? dias, int? _, string esperado)
    {
        var validade = dias is null ? GrantValidity.Forever : GrantValidity.For(TimeSpan.FromDays(dias.Value));

        Assert.Equal(esperado, validade.Describe());
    }

    [Fact]
    public void Descreve_a_validade_com_data_fixa()
    {
        var validade = GrantValidity.Until(new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero));

        Assert.StartsWith("até ", validade.Describe());
    }
}
