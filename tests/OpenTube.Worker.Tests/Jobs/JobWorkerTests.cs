using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Queue;
using OpenTube.TestSupport;
using OpenTube.Worker.Jobs;
using OpenTube.Worker.Tests.Support;

namespace OpenTube.Worker.Tests.Jobs;

/// <summary>Exercita o laço de consumo: retirada, conclusão, falha e retomada.</summary>
[Collection(IntegrationCollection.Name)]
public class JobWorkerTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _relogio = new(Agora);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Executor controlado pelo teste, que conta chamadas e pode falhar sob comando.</summary>
    private sealed class ExecutorDeTeste : IJobHandler
    {
        private readonly TaskCompletionSource _primeiraChamada = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JobKind Kind => JobKind.Transcode;

        public int Chamadas;

        public bool DeveFalhar { get; set; }

        public Task Executado => _primeiraChamada.Task;

        public Task HandleAsync(QueuedJob job, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Chamadas);
            _primeiraChamada.TrySetResult();

            return DeveFalhar
                ? Task.FromException(new InvalidOperationException("falha proposital"))
                : Task.CompletedTask;
        }
    }

    private (ServiceProvider Provider, ExecutorDeTeste Executor) Montar()
    {
        var executor = new ExecutorDeTeste();
        var servicos = new ServiceCollection();

        servicos.AddDbContext<OpenTubeDbContext>(o => o
            .UseNpgsql(postgres.ConnectionString)
            .UseSnakeCaseNamingConvention());

        servicos.AddSingleton<TimeProvider>(_relogio);
        servicos.AddScoped<IJobQueue, PostgresJobQueue>();
        servicos.AddSingleton<IJobHandler>(executor);

        return (servicos.BuildServiceProvider(), executor);
    }

    private JobWorker Criar(IServiceProvider provider) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new WorkerOptions
            {
                WorkerId = "worker-de-teste",
                IdleDelay = TimeSpan.FromMilliseconds(50),
                Lease = TimeSpan.FromMinutes(5),
                RenewInterval = TimeSpan.FromMinutes(2)
            }),
            NullLogger<JobWorker>.Instance);

    private async Task<Guid> EnfileirarAsync(ServiceProvider provider)
    {
        using var escopo = provider.CreateScope();
        var fila = escopo.ServiceProvider.GetRequiredService<IJobQueue>();

        return await fila.EnqueueAsync(JobKind.Transcode, Guid.CreateVersion7(), new { origem = "a.mp4" });
    }

    [Fact]
    public async Task Retira_da_fila_executa_e_conclui()
    {
        var (provider, executor) = Montar();
        await using var _ = provider;
        var jobId = await EnfileirarAsync(provider);

        var worker = Criar(provider);
        using var parada = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await worker.StartAsync(parada.Token);
        await executor.Executado.WaitAsync(parada.Token);
        await worker.StopAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        var job = await db.ProcessingJobs.SingleAsync(j => j.Id == jobId);

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(1, executor.Chamadas);
    }

    [Fact]
    public async Task Falha_do_executor_devolve_o_trabalho_para_a_fila()
    {
        var (provider, executor) = Montar();
        await using var _ = provider;
        executor.DeveFalhar = true;
        var jobId = await EnfileirarAsync(provider);

        var worker = Criar(provider);
        using var parada = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await worker.StartAsync(parada.Token);
        await executor.Executado.WaitAsync(parada.Token);
        await worker.StopAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        var job = await db.ProcessingJobs.SingleAsync(j => j.Id == jobId);

        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Contains("falha proposital", job.LastError);
        Assert.True(job.RunAfter > Agora, "o trabalho deveria voltar com recuo");
    }

    [Fact]
    public async Task Fila_vazia_nao_chama_executor_nenhum()
    {
        var (provider, executor) = Montar();
        await using var _ = provider;

        var worker = Criar(provider);
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, executor.Chamadas);
    }

    /// <summary>Transcrição que só termina quando o teste libera.</summary>
    private sealed class TranscricaoPresa : IJobHandler
    {
        public TaskCompletionSource Comecou { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Liberar { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JobKind Kind => JobKind.Transcript;

        public async Task HandleAsync(QueuedJob job, CancellationToken cancellationToken = default)
        {
            Comecou.TrySetResult();
            await Liberar.Task.WaitAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task Transcricao_longa_nao_segura_a_transcodificacao()
    {
        var executor = new ExecutorDeTeste();
        var transcricao = new TranscricaoPresa();
        var servicos = new ServiceCollection();

        servicos.AddDbContext<OpenTubeDbContext>(o => o
            .UseNpgsql(postgres.ConnectionString)
            .UseSnakeCaseNamingConvention());
        servicos.AddSingleton<TimeProvider>(_relogio);
        servicos.AddScoped<IJobQueue, PostgresJobQueue>();
        servicos.AddSingleton<IJobHandler>(executor);
        servicos.AddSingleton<IJobHandler>(transcricao);

        await using var provider = servicos.BuildServiceProvider();

        using (var escopo = provider.CreateScope())
        {
            var fila = escopo.ServiceProvider.GetRequiredService<IJobQueue>();
            await fila.EnqueueAsync(JobKind.Transcript, Guid.CreateVersion7(), new { idioma = "auto" });
        }

        var worker = Criar(provider);
        using var parada = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await worker.StartAsync(parada.Token);
        await transcricao.Comecou.Task.WaitAsync(parada.Token);

        // Com a transcrição ainda em andamento, um vídeo novo chega e é processado.
        await EnfileirarAsync(provider);
        await executor.Executado.WaitAsync(parada.Token);

        transcricao.Liberar.SetResult();
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, executor.Chamadas);
    }
}
