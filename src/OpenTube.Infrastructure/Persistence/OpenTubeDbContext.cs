using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Entities;

namespace OpenTube.Infrastructure.Persistence;

/// <summary>Contexto de dados da aplicação. O esquema usa nomes em minúsculas com sublinhado.</summary>
public class OpenTubeDbContext(DbContextOptions<OpenTubeDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Video> Videos => Set<Video>();
    public DbSet<VideoAsset> VideoAssets => Set<VideoAsset>();
    public DbSet<ProcessingJob> ProcessingJobs => Set<ProcessingJob>();
    public DbSet<LoginCode> LoginCodes => Set<LoginCode>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<AuthAttempt> AuthAttempts => Set<AuthAttempt>();
    public DbSet<AccessGrant> AccessGrants => Set<AccessGrant>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<CollectionVideo> CollectionVideos => Set<CollectionVideo>();
    public DbSet<VerifiedDomain> VerifiedDomains => Set<VerifiedDomain>();
    public DbSet<PlaybackSession> PlaybackSessions => Set<PlaybackSession>();
    public DbSet<PlaybackInterval> PlaybackIntervals => Set<PlaybackInterval>();
    public DbSet<PlaybackEvent> PlaybackEvents => Set<PlaybackEvent>();
    public DbSet<VideoDailyStat> VideoDailyStats => Set<VideoDailyStat>();
    public DbSet<VideoRetentionBucket> VideoRetentionBuckets => Set<VideoRetentionBucket>();
    public DbSet<SupportThread> SupportThreads => Set<SupportThread>();
    public DbSet<SupportMessage> SupportMessages => Set<SupportMessage>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<PlayerWatermark> PlayerWatermarks => Set<PlayerWatermark>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pg_trgm");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OpenTubeDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
