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
builder.Services.AddScoped<ITranscriber, CommandLineTranscriber>();
builder.Services.AddScoped<IJobHandler, TranscriptionJobHandler>();

builder.Services.AddHostedService<AnalyticsBootstrapper>();
builder.Services.AddHostedService<JobWorker>();

var host = builder.Build();
await host.RunAsync();
