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

        builder.Property(a => a.Status).HasConversion<int>();
        builder.Property(a => a.Source).HasConversion<int>();
        builder.Property(a => a.Error).HasMaxLength(500);
        builder.Ignore(a => a.HasContent);
        builder.Ignore(a => a.IsProcessing);

        builder.HasIndex(a => new { a.VideoId, a.Kind });

        // Uma legenda por idioma em cada vídeo. É também o que impede, no próprio banco, dois
        // pedidos de transcrição do mesmo idioma chegando ao mesmo tempo.
        builder.HasIndex(a => new { a.VideoId, a.Language })
            .IsUnique()
            .HasFilter("kind = 0")
            .HasDatabaseName("ux_video_assets_caption_language");
    }
}
