// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTube.Infrastructure;
using OpenTube.Infrastructure.Playback;
using OpenTube.Infrastructure.Services;
using OpenTube.Infrastructure.Storage;

namespace OpenTube.Infrastructure.Tests.Storage;

/// <summary>
/// O storage é entregue a cada serviço pelo papel que ele usa — leitura, escrita, envio em
/// partes ou preparação —, e não inteiro: quem só entrega arquivos não consegue apagá-los.
/// </summary>
public class PapeisDoStorageTests
{
    private static readonly Type[] Papeis =
        [typeof(IStorageReader), typeof(IStorageWriter), typeof(IMultipartUpload), typeof(IStorageSetup)];

    /// <summary>Tipos de parâmetro dos construtores e métodos públicos e internos de um tipo.</summary>
    private static IEnumerable<Type> Dependencias(Type tipo) =>
        tipo.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Cast<MethodBase>()
            .Concat(tipo.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .SelectMany(m => m.GetParameters())
            .Select(p => p.ParameterType);

    [Fact]
    public void Nenhum_servico_depende_do_storage_inteiro()
    {
        var quemPedeOTodo = typeof(S3VideoStorage).Assembly.GetTypes()
            .Where(t => t != typeof(S3VideoStorage) && !t.IsInterface)
            .Where(t => Dependencias(t).Contains(typeof(IVideoStorage)))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(quemPedeOTodo);
    }

    [Theory]
    [InlineData(typeof(PlaybackService))]
    public void Quem_so_entrega_arquivos_nao_recebe_escrita(Type servico)
    {
        var dependencias = Dependencias(servico).ToList();

        Assert.Contains(typeof(IStorageReader), dependencias);
        Assert.DoesNotContain(typeof(IStorageWriter), dependencias);
        Assert.DoesNotContain(typeof(IVideoStorage), dependencias);
    }

    [Fact]
    public void O_envio_de_video_so_conhece_o_envio_em_partes()
    {
        var dependencias = Dependencias(typeof(VideoUploadService)).ToList();

        Assert.Contains(typeof(IMultipartUpload), dependencias);
        Assert.DoesNotContain(typeof(IStorageWriter), dependencias);
        Assert.DoesNotContain(typeof(IStorageReader), dependencias);
    }

    [Fact]
    public void Cada_papel_resolve_para_a_mesma_instancia_do_storage()
    {
        var configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=localhost;Database=papeis;Username=papeis;Password=papeis",
                ["Storage:Endpoint"] = "http://localhost:9000",
                ["Storage:AccessKey"] = "chave",
                ["Storage:SecretKey"] = "segredo"
            })
            .Build();

        var servicos = new ServiceCollection();
        servicos.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicos.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        servicos.AddOpenTubeInfrastructure(configuracao);

        using var provider = servicos.BuildServiceProvider();
        var todo = provider.GetRequiredService<IVideoStorage>();

        // Um cliente S3 só: cada papel é a mesma instância, e não um cliente novo por papel.
        foreach (var papel in Papeis)
            Assert.Same(todo, provider.GetRequiredService(papel));
    }
}
