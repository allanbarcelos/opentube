using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenTube.Domain.Entities;

namespace OpenTube.Infrastructure.Persistence.Configurations;

public class PlaybackSessionConfiguration : IEntityTypeConfiguration<PlaybackSession>
{
    public void Configure(EntityTypeBuilder<PlaybackSession> builder)
    {
        builder.ToTable("playback_sessions");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.AnonymousId).HasMaxLength(64);
        builder.Property(s => s.OperatingSystem).HasMaxLength(40).IsRequired();
        builder.Property(s => s.Browser).HasMaxLength(40).IsRequired();
        builder.Property(s => s.IpHash).HasMaxLength(64);
        builder.Property(s => s.Country).HasMaxLength(2);
        builder.Property(s => s.Referrer).HasMaxLength(500);
        builder.Property(s => s.MaxQuality).HasMaxLength(20);
        builder.Property(s => s.Device).HasConversion<int>();

        // Consultas do painel: por vídeo e por pessoa, sempre ordenadas no tempo.
        builder.HasIndex(s => new { s.VideoId, s.StartedAt });
        builder.HasIndex(s => new { s.UserId, s.StartedAt });
        builder.HasIndex(s => s.GrantId);
        builder.HasIndex(s => s.LastSeenAt);

        builder.HasMany(s => s.Intervals)
            .WithOne()
            .HasForeignKey(i => i.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(s => s.Intervals).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class PlaybackIntervalConfiguration : IEntityTypeConfiguration<PlaybackInterval>
{
    public void Configure(EntityTypeBuilder<PlaybackInterval> builder)
    {
        builder.ToTable("playback_intervals");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.Id).ValueGeneratedOnAdd();
        builder.HasIndex(i => i.SessionId);
    }
}

public class PlaybackEventConfiguration : IEntityTypeConfiguration<PlaybackEvent>
{
    public void Configure(EntityTypeBuilder<PlaybackEvent> builder)
    {
        builder.ToTable("playback_events");

        // A tabela é particionada por mês, e o PostgreSQL exige a chave de particionamento
        // na chave primária.
        builder.HasKey(e => new { e.At, e.Id });

        builder.Property(e => e.Type).HasConversion<int>();
        builder.Property(e => e.Detail).HasMaxLength(200);
    }
}

public class VideoDailyStatConfiguration : IEntityTypeConfiguration<VideoDailyStat>
{
    public void Configure(EntityTypeBuilder<VideoDailyStat> builder)
    {
        builder.ToTable("video_daily_stats");
        builder.HasKey(s => new { s.VideoId, s.Day });

        builder.HasIndex(s => s.Day);
    }
}

public class VideoRetentionBucketConfiguration : IEntityTypeConfiguration<VideoRetentionBucket>
{
    public void Configure(EntityTypeBuilder<VideoRetentionBucket> builder)
    {
        builder.ToTable("video_retention_buckets");
        builder.HasKey(b => new { b.VideoId, b.BucketIndex });
    }
}
