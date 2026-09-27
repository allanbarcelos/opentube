using OpenTube.Infrastructure;
using OpenTube.Worker.Jobs;
using OpenTube.Worker.Media;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables("OPENTUBE_");
// Mesma fonte da aplicação: no Swarm o segredo é um arquivo, não uma variável.
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

builder.Services.AddOpenTubeInfrastructure(builder.Configuration);

builder.Services.Configure<MediaToolOptions>(builder.Configuration.GetSection(MediaToolOptions.SectionName));
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.SectionName));
builder.Services.Configure<AnalyticsOptions>(builder.Configuration.GetSection(AnalyticsOptions.SectionName));
builder.Services.Configure<TranscriptionOptions>(builder.Configuration.GetSection(TranscriptionOptions.SectionName));

builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddScoped<IMediaProbe, FfprobeMediaProbe>();
builder.Services.AddScoped<TranscodePipeline>();
builder.Services.AddScoped<IJobHandler, TranscodeJobHandler>();
builder.Services.AddScoped<IJobHandler, RetireOutputsJobHandler>();
builder.Services.AddScoped<IJobHandler, AnalyticsRollupJobHandler>();
// O servidor do Whisper (o container dedicado) tem preferência; sem ele, o programa de linha
// de comando, se houver; sem nenhum dos dois, a legenda automática fica desligada.
builder.Services.AddSingleton<WhisperHttp>();
builder.Services.AddScoped<ITranscriber>(sp =>
    string.IsNullOrWhiteSpace(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TranscriptionOptions>>().Value.ServerUrl)
        ? ActivatorUtilities.CreateInstance<CommandLineTranscriber>(sp)
        : ActivatorUtilities.CreateInstance<WhisperServerTranscriber>(sp));
builder.Services.AddScoped<IJobHandler, TranscriptionJobHandler>();

builder.Services.AddHostedService<AnalyticsBootstrapper>();
builder.Services.AddHostedService<JobWorker>();
builder.Services.AddHostedService<TranscriptionHeartbeat>();

var host = builder.Build();
await host.RunAsync();
