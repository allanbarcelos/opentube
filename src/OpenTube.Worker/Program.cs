using OpenTube.Infrastructure;
using OpenTube.Worker.Jobs;
using OpenTube.Worker.Media;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables("OPENTUBE_");

builder.Services.AddOpenTubeInfrastructure(builder.Configuration);

builder.Services.Configure<MediaToolOptions>(builder.Configuration.GetSection(MediaToolOptions.SectionName));
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.SectionName));
builder.Services.Configure<AnalyticsOptions>(builder.Configuration.GetSection(AnalyticsOptions.SectionName));

builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddScoped<IMediaProbe, FfprobeMediaProbe>();
builder.Services.AddScoped<TranscodePipeline>();
builder.Services.AddScoped<IJobHandler, TranscodeJobHandler>();
builder.Services.AddScoped<IJobHandler, AnalyticsRollupJobHandler>();

builder.Services.AddHostedService<AnalyticsBootstrapper>();
builder.Services.AddHostedService<JobWorker>();

var host = builder.Build();
await host.RunAsync();
