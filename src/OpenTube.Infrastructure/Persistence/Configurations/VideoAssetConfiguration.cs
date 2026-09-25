using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenTube.Domain.Entities;

namespace OpenTube.Infrastructure.Persistence.Configurations;

public class VideoAssetConfiguration : IEntityTypeConfiguration<VideoAsset>
{
    public void Configure(EntityTypeBuilder<VideoAsset> builder)
    {
        builder.ToTable("video_assets");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.StorageKey).HasMaxLength(500).IsRequired();
        builder.Property(a => a.Language).HasMaxLength(20);
        builder.Property(a => a.Label).HasMaxLength(120);
        builder.Property(a => a.Kind).HasConversion<int>();

        builder.HasIndex(a => new { a.VideoId, a.Kind });
    }
}
