// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Media;

namespace OpenTube.Infrastructure.Persistence.Configurations;

public class VideoChapterConfiguration : IEntityTypeConfiguration<VideoChapter>
{
    public void Configure(EntityTypeBuilder<VideoChapter> builder)
    {
        builder.ToTable("video_chapters");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Title).HasMaxLength(VideoChapters.MaxTitleLength).IsRequired();

        // Excluir o vídeo leva o sumário junto.
        builder.HasOne<Video>().WithMany().HasForeignKey(c => c.VideoId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => new { c.VideoId, c.Position }).IsUnique();
    }
}
