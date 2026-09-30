// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Branding;
using OpenTube.Infrastructure.Playback;
using OpenTube.Infrastructure.Queue;
using OpenTube.Infrastructure.Access;
using OpenTube.Infrastructure.Analytics;
using OpenTube.Infrastructure.Email;
using OpenTube.Infrastructure.Security;
using OpenTube.Infrastructure.Services;
using OpenTube.Infrastructure.Storage;
using OpenTube.Infrastructure.Support;

namespace OpenTube.Infrastructure;

/// <summary>Registro dos serviços de infraestrutura compartilhados pela aplicação e pelo worker.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddOpenTubeInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Vazio não é "ausente": um appsettings sem senha deixaria o EF tentar entrar
        // com uma cadeia em branco em vez de falhar com uma mensagem útil.
        var connectionString = configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = configuration["OPENTUBE_DB"];
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Cadeia de conexão do banco não configurada (ConnectionStrings:Default ou OPENTUBE_DB).");

        services.AddDbContext<OpenTubeDbContext>(options => options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(OpenTubeDbContext).Assembly.FullName))
            .UseSnakeCaseNamingConvention());

        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<SecurityOptions>()
            .Bind(configuration.GetSection(SecurityOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));

        services.TryAddTimeProvider();
        services.AddScoped<IJobQueue, PostgresJobQueue>();
        services.AddSingleton<IVideoStorage, S3VideoStorage>();
        services.AddScoped<VideoUploadService>();
        services.AddScoped<AdminVideoService>();
        services.AddScoped<ProcessingQueueService>();
        services.AddScoped<VideoChapterService>();
        services.AddScoped<VideoRatingService>();
        services.AddScoped<CollectionService>();
        services.AddScoped<CollectionFavoriteService>();
        services.AddScoped<VideoFavoriteService>();
        services.AddScoped<CollectionNoticeService>();
        services.AddScoped<CollectionThumbnailService>();
        services.AddScoped<CaptionService>();
        services.AddScoped<IEmailSender, SmtpEmailSender>();
        services.AddScoped<IAuthRateLimiter, AuthRateLimiter>();
        services.AddScoped<PasswordlessAuthService>();
        services.AddSingleton<PrivacyHasher>();
        services.AddScoped<AdminSeeder>();
        services.AddScoped<AuditTrail>();
        services.AddScoped<AccessService>();
        services.AddScoped<GrantService>();
        services.AddScoped<PlaybackGuard>();
        services.AddScoped<PlaybackService>();
        services.AddSingleton<PlaybackTickets>();
        services.AddScoped<AnalyticsCollector>();
        services.AddScoped<AnalyticsAggregator>();
        services.AddScoped<AnalyticsQueries>();
        services.AddScoped<SupportService>();
        services.AddScoped<VideoCatalog>();
        services.AddScoped<WatermarkService>();
        services.AddScoped<OpenTube.Infrastructure.Transcription.TranscriptionAvailability>();

        return services;
    }

    private static IServiceCollection TryAddTimeProvider(this IServiceCollection services)
    {
        if (services.All(s => s.ServiceType != typeof(TimeProvider)))
            services.AddSingleton(TimeProvider.System);

        return services;
    }
}
