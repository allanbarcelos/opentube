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
using OpenTube.Infrastructure.Storage;
using OpenTube.Infrastructure.Support;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Support2;

[Collection(IntegrationCollection.Name)]
public class SupportServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _relogio = new(Agora);
    private readonly FakeEmailSender _emails = new();

    private readonly SecurityOptions _seguranca = new()
    {
        TokenPepper = "segredo",
        IpHashPepper = "segredo-ip",
        PublicUrl = "https://opentube.org"
    };

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private (SupportService Servico, OpenTubeDbContext Db) Criar()
    {
        var db = postgres.CreateContext();
        var opcoes = Microsoft.Extensions.Options.Options.Create(_seguranca);

        return (new SupportService(
            db, new AccessService(db, opcoes, _relogio), _emails, opcoes, _relogio,
            NullLogger<SupportService>.Instance), db);
    }

    private async Task<Video> CriarVideoAsync(VideoVisibility visibilidade = VideoVisibility.Public)
    {
        await using var db = postgres.CreateContext();
        var videoId = Guid.CreateVersion7();

        var video = Video.CreateDraft("Reunião Trimestral", $"v-{videoId:n}"[..20], "originals/a.mp4",
            Guid.CreateVersion7(), Agora, id: videoId);
        video.MarkUploaded(1024);
        video.StartProcessing();
        video.MarkReady(StorageKeys.VodPrefix(videoId), 600, 1280, 720, null, null, Agora);
        if (visibilidade is not VideoVisibility.Private)
            video.ChangeVisibility(visibilidade);

        db.Videos.Add(video);
        await db.SaveChangesAsync();

        return video;
    }

    private async Task<Viewer> CriarPessoaAsync(string email, bool admin = false)
    {
        await using var db = postgres.CreateContext();
        var usuario = User.Create(EmailAddress.Parse(email), Agora, admin);

        db.Users.Add(usuario);
        await db.SaveChangesAsync();

        return Viewer.From(usuario);
    }

    [Fact]
    public async Task Abre_uma_conversa_e_avisa_a_administracao()
    {
        var video = await CriarVideoAsync();
        await CriarPessoaAsync("admin@opentube.org", admin: true);
        var pessoa = await CriarPessoaAsync("allan@barcelos.dev");

        var (servico, db) = Criar();
        await using var _ = db;

        var conversa = await servico.OpenAsync(video.Id, pessoa, "Não consigo ouvir o áudio", 83);

        Assert.Equal(SupportStatus.Open, conversa.Status);
        Assert.Equal(83, conversa.TimestampSeconds);

        var aviso = Assert.Single(_emails.Sent);
        Assert.Equal("admin@opentube.org", aviso.To);
        Assert.Contains("Reunião Trimestral", aviso.Subject);
        Assert.Contains("Não consigo ouvir o áudio", aviso.TextBody);
        Assert.Contains($"/admin/suporte/{conversa.Id}", aviso.TextBody);
    }

    [Fact]
    public async Task So_quem_pode_assistir_consegue_abrir_conversa()
    {
        var video = await CriarVideoAsync(VideoVisibility.Private);
        var pessoa = await CriarPessoaAsync("allan@barcelos.dev");

        var (servico, db) = Criar();
        await using var _ = db;

        // A conversa não pode virar um canal para sondar o acervo.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servico.OpenAsync(video.Id, pessoa, "Existe este vídeo?"));

        Assert.Empty(_emails.Sent);
    }

    [Fact]
    public async Task Visitante_anonimo_nao_abre_conversa()
    {
        var video = await CriarVideoAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servico.OpenAsync(video.Id, Viewer.Anonymous, "Olá"));
    }

    [Fact]
    public async Task Video_inexistente_nao_abre_conversa()
    {
        var pessoa = await CriarPessoaAsync("allan@barcelos.dev");
        var (servico, db) = Criar();
        await using var _ = db;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servico.OpenAsync(Guid.CreateVersion7(), pessoa, "Olá"));
    }

    [Fact]
    public async Task A_resposta_da_administracao_avisa_quem_perguntou()
    {
        var video = await CriarVideoAsync();
        var admin = await CriarPessoaAsync("admin@opentube.org", admin: true);
        var pessoa = await CriarPessoaAsync("allan@barcelos.dev");

        var (servico, db) = Criar();
        await using var _ = db;
        var conversa = await servico.OpenAsync(video.Id, pessoa, "Não consigo ouvir o áudio");
        _emails.Clear();

        await servico.ReplyAsync(conversa.Id, admin, "O áudio está no segundo canal");

        var aviso = Assert.Single(_emails.Sent);
        Assert.Equal("allan@barcelos.dev", aviso.To);
        Assert.Contains("segundo canal", aviso.TextBody);
        Assert.Contains($"/v/{video.Slug}", aviso.TextBody);

        await using var leitura = postgres.CreateContext();
        Assert.Equal(SupportStatus.Answered, (await leitura.SupportThreads.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_replica_de_quem_perguntou_volta_para_a_administracao()
    {
        var video = await CriarVideoAsync();
        var admin = await CriarPessoaAsync("admin@opentube.org", admin: true);
        var pessoa = await CriarPessoaAsync("allan@barcelos.dev");

        var (servico, db) = Criar();
        await using var _ = db;
        var conversa = await servico.OpenAsync(video.Id, pessoa, "Primeira");
        await servico.ReplyAsync(conversa.Id, admin, "Resposta");
        _emails.Clear();

        await servico.ReplyAsync(conversa.Id, pessoa, "Continua com problema");

        Assert.Equal("admin@opentube.org", _emails.Last!.To);

        await using var leitura = postgres.CreateContext();
        var gravada = await leitura.SupportThreads.Include(t => t.Messages).SingleAsync();

        Assert.Equal(SupportStatus.Open, gravada.Status);
        Assert.Equal(3, gravada.Messages.Count);
    }

    [Fact]
    public async Task Uma_pessoa_nao_alcanca_a_conversa_de_outra()
    {
        var video = await CriarVideoAsync();
        await CriarPessoaAsync("admin@opentube.org", admin: true);
        var dona = await CriarPessoaAsync("dona@barcelos.dev");
        var intrusa = await CriarPessoaAsync("intrusa@barcelos.dev");

        var (servico, db) = Criar();
        await using var _ = db;
        var conversa = await servico.OpenAsync(video.Id, dona, "Assunto privado");

        Assert.Null(await servico.FindAsync(conversa.Id, intrusa));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servico.ReplyAsync(conversa.Id, intrusa, "Intrusão"));
    }

    [Fact]
    public async Task A_listagem_por_video_mostra_apenas_as_proprias_conversas()
    {
        var video = await CriarVideoAsync();
        var admin = await CriarPessoaAsync("admin@opentube.org", admin: true);
        var uma = await CriarPessoaAsync("uma@barcelos.dev");
        var outra = await CriarPessoaAsync("outra@barcelos.dev");

        var (servico, db) = Criar();
        await using var _ = db;
        await servico.OpenAsync(video.Id, uma, "Pergunta da primeira");
        await servico.OpenAsync(video.Id, outra, "Pergunta da segunda");

        var daPrimeira = await servico.ForVideoAsync(video.Id, uma);
        Assert.Single(daPrimeira);
        Assert.Equal("Pergunta da primeira", daPrimeira[0].Messages[0].Body);

        // A administração enxerga as duas.
        Assert.Equal(2, (await servico.ForVideoAsync(video.Id, admin)).Count);
    }

    [Fact]
    public async Task Visitante_anonimo_nao_ve_conversa_nenhuma()
    {
        var video = await CriarVideoAsync();
        await CriarPessoaAsync("admin@opentube.org", admin: true);
        var pessoa = await CriarPessoaAsync("allan@barcelos.dev");

        var (servico, db) = Criar();
        await using var _ = db;
        await servico.OpenAsync(video.Id, pessoa, "Privado");

        Assert.Empty(await servico.ForVideoAsync(video.Id, Viewer.Anonymous));
    }

    [Fact]
    public async Task A_fila_da_administracao_traz_o_contexto_de_cada_conversa()
    {
        var video = await CriarVideoAsync();
        await CriarPessoaAsync("admin@opentube.org", admin: true);
        var pessoa = await CriarPessoaAsync("allan@barcelos.dev");

        var (servico, db) = Criar();
        await using var _ = db;
        await servico.OpenAsync(video.Id, pessoa, "Pergunta");

        var fila = await servico.QueueAsync();
        var item = Assert.Single(fila);

        Assert.Equal("Reunião Trimestral", item.VideoTitle);
        Assert.Equal("allan@barcelos.dev", item.UserEmail);
        Assert.Equal(video.Slug, item.VideoSlug);
    }

    [Fact]
    public async Task A_fila_filtra_por_situacao()
    {
        var video = await CriarVideoAsync();
        var admin = await CriarPessoaAsync("admin@opentube.org", admin: true);
        var pessoa = await CriarPessoaAsync("allan@barcelos.dev");

        var (servico, db) = Criar();
        await using var _ = db;
        var respondida = await servico.OpenAsync(video.Id, pessoa, "Respondida");
        await servico.OpenAsync(video.Id, pessoa, "Pendente");
        await servico.ReplyAsync(respondida.Id, admin, "Resposta");

        Assert.Single(await servico.QueueAsync(SupportStatus.Open));
        Assert.Single(await servico.QueueAsync(SupportStatus.Answered));
        Assert.Equal(1, await servico.PendingCountAsync());
    }

    [Fact]
    public async Task Encerrar_e_reabrir_a_conversa()
    {
        var video = await CriarVideoAsync();
        var admin = await CriarPessoaAsync("admin@opentube.org", admin: true);
        var pessoa = await CriarPessoaAsync("allan@barcelos.dev");

        var (servico, db) = Criar();
        await using var _ = db;
        var conversa = await servico.OpenAsync(video.Id, pessoa, "Pergunta");

        await servico.CloseAsync(conversa.Id, admin);
        Assert.True((await servico.FindAsync(conversa.Id, admin))!.IsClosed);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servico.ReplyAsync(conversa.Id, pessoa, "Mais uma"));

        await servico.ReopenAsync(conversa.Id, admin);
        Assert.False((await servico.FindAsync(conversa.Id, admin))!.IsClosed);
    }

    [Fact]
    public async Task Marcar_como_lida_limpa_a_pendencia_do_lado_certo()
    {
        var video = await CriarVideoAsync();
        var admin = await CriarPessoaAsync("admin@opentube.org", admin: true);
        var pessoa = await CriarPessoaAsync("allan@barcelos.dev");

        var (servico, db) = Criar();
        await using var _ = db;
        var conversa = await servico.OpenAsync(video.Id, pessoa, "Pergunta");

        Assert.True((await servico.FindAsync(conversa.Id, admin))!.NeedsAdminAttention);

        await servico.MarkReadAsync(conversa.Id, admin);

        Assert.False((await servico.FindAsync(conversa.Id, admin))!.NeedsAdminAttention);
    }

    [Fact]
    public async Task Conversa_inexistente_responde_como_conversa_alheia()
    {
        var pessoa = await CriarPessoaAsync("allan@barcelos.dev");
        var (servico, db) = Criar();
        await using var _ = db;

        Assert.Null(await servico.FindAsync(Guid.CreateVersion7(), pessoa));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servico.CloseAsync(Guid.CreateVersion7(), pessoa));
    }

    [Fact]
    public async Task O_aviso_leva_so_o_comeco_da_mensagem()
    {
        var video = await CriarVideoAsync();
        await CriarPessoaAsync("admin@opentube.org", admin: true);
        var pessoa = await CriarPessoaAsync("allan@barcelos.dev");

        var (servico, db) = Criar();
        await using var _ = db;

        await servico.OpenAsync(video.Id, pessoa, new string('x', 1000));

        // A conversa inteira fica na plataforma, onde o acesso é conferido.
        Assert.Contains("…", _emails.Last!.TextBody);
        Assert.True(_emails.Last.TextBody.Length < 1000);
    }
}
