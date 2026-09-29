// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenTube.Infrastructure.Email;
using OpenTube.TestSupport;

namespace OpenTube.Web.Tests.Support;

/// <summary>
/// Sobe a aplicação inteira contra o banco e o storage efêmeros, trocando apenas o envio de
/// email por um substituto que guarda as mensagens em memória.
/// </summary>
public class OpenTubeWebFactory(PostgresFixture postgres, MinioFixture minio, params string[] administradores)
    : WebApplicationFactory<Program>
{
    public FakeEmailSender Emails { get; } = new();

    /// <summary>Entrega os segmentos por caminho autorizado, em vez de endereço assinado.</summary>
    public bool ComAutorizacaoDeSegmento { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.UseSetting("ConnectionStrings:Default", postgres.ConnectionString);
        builder.UseSetting("Storage:Endpoint", minio.Options.Endpoint);
        builder.UseSetting("Storage:AccessKey", minio.Options.AccessKey);
        builder.UseSetting("Storage:SecretKey", minio.Options.SecretKey);
        builder.UseSetting("Storage:OriginalsBucket", minio.Options.OriginalsBucket);
        builder.UseSetting("Storage:VodBucket", minio.Options.VodBucket);
        builder.UseSetting("Storage:SegmentAuthorization", ComAutorizacaoDeSegmento ? "true" : "false");
        builder.UseSetting("Security:TokenPepper", "segredo-de-teste");
        builder.UseSetting("Security:IpHashPepper", "segredo-de-ip");
        builder.UseSetting("Security:PublicUrl", "http://localhost");

        for (var i = 0; i < administradores.Length; i++)
            builder.UseSetting($"Security:AdminEmails:{i}", administradores[i]);

        builder.ConfigureLogging(log => log.AddConsole().SetMinimumLevel(LogLevel.Warning));

        builder.ConfigureServices(servicos =>
        {
            servicos.RemoveAll<IEmailSender>();
            servicos.AddSingleton<IEmailSender>(Emails);
        });
    }

    /// <summary>Cliente que segue redirecionamentos e guarda os cookies, como um navegador.</summary>
    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });
}
