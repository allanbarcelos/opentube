using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Storage;

namespace OpenTube.TestSupport;

/// <summary>
/// Sobe um storage compatível com S3 para os testes. Assinatura de URL e envio multipart só
/// se comprovam contra um servidor de verdade.
/// </summary>
public class MinioFixture : IAsyncLifetime
{
    private const string Acesso = "opentube";
    private const string Segredo = "opentube123";

    private readonly IContainer _container = new ContainerBuilder("bitnamilegacy/minio:latest")
        .WithEnvironment("MINIO_ROOT_USER", Acesso)
        .WithEnvironment("MINIO_ROOT_PASSWORD", Segredo)
        .WithEnvironment("MINIO_SCHEME", "http")
        .WithPortBinding(9000, true)
        // A imagem da Bitnami sobe o MinIO uma vez para se configurar, derruba e sobe de novo
        // (uns 5 segundos depois). O primeiro servidor já responde ao teste de saúde: esperar só
        // por ele deixava os testes começarem em cima da troca, e a requisição em andamento
        // morria ("Client disconnected before response was ready" / "response ended
        // prematurely"). A mensagem abaixo marca o servidor definitivo.
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilMessageIsLogged(@"\*\* Starting MinIO \*\*")
            .UntilHttpRequestIsSucceeded(r => r.ForPort(9000).ForPath("/minio/health/live")))
        .Build();

    public string Endpoint => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(9000)}";

    public StorageOptions Options { get; private set; } = new();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        Options = new StorageOptions
        {
            Endpoint = Endpoint,
            AccessKey = Acesso,
            SecretKey = Segredo,
            OriginalsBucket = "originals",
            VodBucket = "vod",
            UploadUrlLifetime = TimeSpan.FromMinutes(30),
            PlaybackUrlLifetime = TimeSpan.FromMinutes(30)
        };

        using var storage = CreateStorage();
        await storage.EnsureBucketsAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public S3VideoStorage CreateStorage() =>
        new(Microsoft.Extensions.Options.Options.Create(Options), NullLogger<S3VideoStorage>.Instance);
}

