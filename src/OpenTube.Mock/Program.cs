// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTube.Infrastructure;
using OpenTube.Mock;
using OpenTube.Worker.Media;

if (args.Any(a => a is "--help" or "-h" or "help"))
{
    Console.WriteLine(Pedido.Ajuda);
    return;
}

Plano plano;

try
{
    plano = Catalogo.Montar(Pedido.Analisar(args));
}
catch (UsoInvalidoException e)
{
    Console.Error.WriteLine(e.Message);
    Environment.ExitCode = 1;
    return;
}

// Mesmas variáveis do make watch (scripts/dev-env.sh) e os mesmos administradores da
// aplicação no host: o appsettings de Development, com o ambiente por cima.
var desenvolvimento = AcharConfiguracaoDeDesenvolvimento();

var builder = Host.CreateApplicationBuilder(Array.Empty<string>());

builder.Configuration
    .AddJsonFile(desenvolvimento, optional: false, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddEnvironmentVariables("OPENTUBE_");

builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);

builder.Services.AddOpenTubeInfrastructure(builder.Configuration);
builder.Services.Configure<MediaToolOptions>(builder.Configuration.GetSection(MediaToolOptions.SectionName));
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddScoped<IMediaProbe, FfprobeMediaProbe>();
builder.Services.AddScoped<TranscodePipeline>();
builder.Services.AddScoped<Gerador>();

var host = builder.Build();
await host.StartAsync();

try
{
    await using var escopo = host.Services.CreateAsyncScope();
    await escopo.ServiceProvider.GetRequiredService<Gerador>().ExecutarAsync(plano);
}
finally
{
    await host.StopAsync();
}

static string AcharConfiguracaoDeDesenvolvimento()
{
    foreach (var inicio in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var diretorio = new DirectoryInfo(inicio);

        while (diretorio is not null)
        {
            var candidato = Path.Combine(diretorio.FullName, "src", "OpenTube.Web", "appsettings.Development.json");

            if (File.Exists(candidato))
                return candidato;

            diretorio = diretorio.Parent;
        }
    }

    throw new InvalidOperationException(
        "Não encontrei src/OpenTube.Web/appsettings.Development.json. Rode make mock na raiz do repositório.");
}
