// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenTube.Domain.Entities;

namespace OpenTube.Infrastructure.Persistence.Configurations;

public class VideoConfiguration : IEntityTypeConfiguration<Video>
{
    public void Configure(EntityTypeBuilder<Video> builder)
    {
        builder.ToTable("videos");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.Title).HasMaxLength(300).IsRequired();
        builder.Property(v => v.Description).HasMaxLength(10_000);
        builder.Property(v => v.Slug).HasMaxLength(80).IsRequired();
        builder.Property(v => v.OriginalKey).HasMaxLength(500).IsRequired();
        builder.Property(v => v.HlsPrefix).HasMaxLength(500);
        builder.Property(v => v.ThumbnailKey).HasMaxLength(500);
        builder.Property(v => v.SpriteKey).HasMaxLength(500);

        builder.Property(v => v.Visibility).HasConversion<int>();
        builder.Property(v => v.Status).HasConversion<int>();

        builder.Property<List<string>>("_tags")
            .HasColumnName("tags")
            .HasColumnType("text[]")
            .IsRequired();
        builder.Ignore(v => v.Tags);

        builder.HasIndex(v => v.Slug).IsUnique();
        builder.HasIndex(v => new { v.Visibility, v.Status });
        builder.HasIndex(v => v.CreatedAt);

        builder.HasMany(v => v.Assets)
            .WithOne()
            .HasForeignKey(a => a.VideoId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(v => v.Assets).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
