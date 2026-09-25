using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Queue;
using OpenTube.Infrastructure.Tests.Support;

namespace OpenTube.Infrastructure.Tests.Queue;

[Collection(IntegrationCollection.Name)]
public class PostgresJobQueueTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Reserva = TimeSpan.FromMinutes(10);
    private static readonly JobKind[] Transcodificacao = [JobKind.Transcode];

    private readonly FakeTimeProvider _relogio = new(Agora);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private (PostgresJobQueue Fila, OpenTubeDbContext Db) Criar()
    {
        var db = fixture.CreateContext();
        return (new PostgresJobQueue(db, _relogio), db);
    }

    [Fact]
    public async Task Enfileira_e_retira_o_trabalho()
    {
        var (fila, db) = Criar();
        await using var _ = db;
        var alvo = Guid.CreateVersion7();

        var id = await fila.EnqueueAsync(JobKind.Transcode, alvo, new { origem = "originals/a.mp4" });
        var job = await fila.DequeueAsync("worker-1", Transcodificacao, Reserva);

        Assert.NotNull(job);
        Assert.Equal(id, job.Id);
        Assert.Equal(JobKind.Transcode, job.Kind);
        Assert.Equal(alvo, job.TargetId);
        Assert.Equal(1, job.Attempts);
        Assert.Contains("originals/a.mp4", job.Payload);
    }

    [Fact]
    public async Task Le_o_payload_no_tipo_esperado()
    {
        var (fila, db) = Criar();
        await using var _ = db;
        await fila.EnqueueAsync(JobKind.Transcode, payload: new { Origem = "originals/a.mp4", Tentativa = 2 });

        var job = await fila.DequeueAsync("worker-1", Transcodificacao, Reserva);
        var payload = job!.PayloadAs<PayloadTeste>();

        Assert.Equal("originals/a.mp4", payload!.Origem);
        Assert.Equal(2, payload.Tentativa);
    }

    [Fact]
    public async Task Devolve_nulo_quando_a_fila_esta_vazia()
    {
        var (fila, db) = Criar();
        await using var _ = db;

        Assert.Null(await fila.DequeueAsync("worker-1", Transcodificacao, Reserva));
    }

    [Fact]
    public async Task Ignora_trabalho_de_outro_tipo()
    {
        var (fila, db) = Criar();
        await using var _ = db;
        await fila.EnqueueAsync(JobKind.AnalyticsRollup);

        Assert.Null(await fila.DequeueAsync("worker-1", Transcodificacao, Reserva));
    }

    [Fact]
    public async Task Nao_entrega_trabalho_agendado_para_o_futuro()
    {
        var (fila, db) = Criar();
        await using var _ = db;
        await fila.EnqueueAsync(JobKind.Transcode, delay: TimeSpan.FromMinutes(5));

        Assert.Null(await fila.DequeueAsync("worker-1", Transcodificacao, Reserva));

        _relogio.Advance(TimeSpan.FromMinutes(6));

        Assert.NotNull(await fila.DequeueAsync("worker-1", Transcodificacao, Reserva));
    }

    [Fact]
    public async Task Dois_workers_nunca_pegam_o_mesmo_trabalho()
    {
        var (fila, db) = Criar();
        await using var _ = db;
        await fila.EnqueueAsync(JobKind.Transcode);

        var primeiro = await fila.DequeueAsync("worker-1", Transcodificacao, Reserva);
        var segundo = await fila.DequeueAsync("worker-2", Transcodificacao, Reserva);

        Assert.NotNull(primeiro);
        Assert.Null(segundo);
    }

    [Fact]
    public async Task Entrega_na_ordem_de_disponibilidade()
    {
        var (fila, db) = Criar();
        await using var _ = db;

        var segundo = await fila.EnqueueAsync(JobKind.Transcode, delay: TimeSpan.FromMinutes(1));
        var primeiro = await fila.EnqueueAsync(JobKind.Transcode);

        _relogio.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(primeiro, (await fila.DequeueAsync("worker-1", Transcodificacao, Reserva))!.Id);
        Assert.Equal(segundo, (await fila.DequeueAsync("worker-2", Transcodificacao, Reserva))!.Id);
    }

    [Fact]
    public async Task Recupera_trabalho_de_worker_que_morreu_no_meio()
    {
        var (fila, db) = Criar();
        await using var _ = db;
        await fila.EnqueueAsync(JobKind.Transcode);

        var perdido = await fila.DequeueAsync("worker-morto", Transcodificacao, Reserva);
        Assert.NotNull(perdido);

        // Antes da reserva vencer, ninguém mais encosta no trabalho.
        Assert.Null(await fila.DequeueAsync("worker-2", Transcodificacao, Reserva));

        _relogio.Advance(Reserva + TimeSpan.FromMinutes(1));

        var resgatado = await fila.DequeueAsync("worker-2", Transcodificacao, Reserva);

        Assert.NotNull(resgatado);
        Assert.Equal(perdido.Id, resgatado.Id);
        Assert.Equal(2, resgatado.Attempts);
    }

    [Fact]
    public async Task Renovar_a_reserva_impede_o_resgate()
    {
        var (fila, db) = Criar();
        await using var _ = db;
        await fila.EnqueueAsync(JobKind.Transcode);
        var job = await fila.DequeueAsync("worker-1", Transcodificacao, Reserva);

        _relogio.Advance(TimeSpan.FromMinutes(9));
        Assert.True(await fila.RenewAsync(job!.Id, "worker-1", Reserva));

        _relogio.Advance(TimeSpan.FromMinutes(5));

        Assert.Null(await fila.DequeueAsync("worker-2", Transcodificacao, Reserva));
    }

    [Fact]
    public async Task Nao_renova_reserva_de_outro_worker()
    {
        var (fila, db) = Criar();
        await using var _ = db;
        await fila.EnqueueAsync(JobKind.Transcode);
        var job = await fila.DequeueAsync("worker-1", Transcodificacao, Reserva);

        Assert.False(await fila.RenewAsync(job!.Id, "worker-2", Reserva));
    }

    [Fact]
    public async Task Concluir_tira_o_trabalho_da_fila_em_definitivo()
    {
        var (fila, db) = Criar();
        await using var _ = db;
        await fila.EnqueueAsync(JobKind.Transcode);
        var job = await fila.DequeueAsync("worker-1", Transcodificacao, Reserva);

        await fila.CompleteAsync(job!.Id);
        _relogio.Advance(TimeSpan.FromHours(1));

        Assert.Null(await fila.DequeueAsync("worker-2", Transcodificacao, Reserva));

        var contagem = await fila.CountByStatusAsync();
        Assert.Equal(1, contagem[JobStatus.Succeeded]);
    }

    [Fact]
    public async Task Falha_recuperavel_devolve_o_trabalho_com_recuo()
    {
        var (fila, db) = Criar();
        await using var _ = db;
        await fila.EnqueueAsync(JobKind.Transcode);
        var job = await fila.DequeueAsync("worker-1", Transcodificacao, Reserva);

        await fila.FailAsync(job!.Id, "ffmpeg saiu com código 1");

        // O recuo da primeira tentativa é de 30 segundos.
        _relogio.Advance(TimeSpan.FromSeconds(20));
        Assert.Null(await fila.DequeueAsync("worker-1", Transcodificacao, Reserva));

        _relogio.Advance(TimeSpan.FromSeconds(20));
        var retomado = await fila.DequeueAsync("worker-1", Transcodificacao, Reserva);

        Assert.NotNull(retomado);
        Assert.Equal(2, retomado.Attempts);
    }

    [Fact]
    public async Task Desiste_depois_do_limite_de_tentativas()
    {
        var (fila, db) = Criar();
        await using var _ = db;
        await fila.EnqueueAsync(JobKind.Transcode);

        for (var tentativa = 1; tentativa <= 3; tentativa++)
        {
            var job = await fila.DequeueAsync("worker-1", Transcodificacao, Reserva);
            Assert.NotNull(job);
            await fila.FailAsync(job.Id, "erro persistente");
            _relogio.Advance(TimeSpan.FromHours(1));
        }

        Assert.Null(await fila.DequeueAsync("worker-1", Transcodificacao, Reserva));

        var contagem = await fila.CountByStatusAsync();
        Assert.Equal(1, contagem[JobStatus.Failed]);
    }

    [Fact]
    public async Task Trunca_mensagem_de_erro_muito_longa()
    {
        var (fila, db) = Criar();
        await using var _ = db;
        await fila.EnqueueAsync(JobKind.Transcode);
        var job = await fila.DequeueAsync("worker-1", Transcodificacao, Reserva);

        await fila.FailAsync(job!.Id, new string('x', 9000));

        await using var leitura = fixture.CreateContext();
        var registro = await leitura.ProcessingJobs.SingleAsync(j => j.Id == job.Id);

        Assert.Equal(4000, registro.LastError!.Length);
    }

    [Fact]
    public async Task Conta_os_trabalhos_por_estado()
    {
        var (fila, db) = Criar();
        await using var _ = db;
        await fila.EnqueueAsync(JobKind.Transcode);
        await fila.EnqueueAsync(JobKind.Transcode);
        var job = await fila.DequeueAsync("worker-1", Transcodificacao, Reserva);
        await fila.CompleteAsync(job!.Id);

        var contagem = await fila.CountByStatusAsync();

        Assert.Equal(1, contagem[JobStatus.Pending]);
        Assert.Equal(1, contagem[JobStatus.Succeeded]);
    }

    [Fact]
    public async Task Exige_identificacao_do_worker()
    {
        var (fila, db) = Criar();
        await using var _ = db;

        await Assert.ThrowsAnyAsync<ArgumentException>(() => fila.DequeueAsync("   ", Transcodificacao, Reserva));
    }

    [Fact]
    public async Task Lista_vazia_de_tipos_nao_retira_nada()
    {
        var (fila, db) = Criar();
        await using var _ = db;
        await fila.EnqueueAsync(JobKind.Transcode);

        Assert.Null(await fila.DequeueAsync("worker-1", [], Reserva));
    }

    private sealed record PayloadTeste(string Origem, int Tentativa);
}
