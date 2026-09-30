// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenTube.Domain.Entities;

namespace OpenTube.Infrastructure.Persistence.Configurations;

public class VideoFavoriteConfiguration : IEntityTypeConfiguration<VideoFavorite>
{
    public void Configure(EntityTypeBuilder<VideoFavorite> builder)
    {
        builder.ToTable("video_favorites");

        // Uma marcação por pessoa e vídeo. O vídeo vem primeiro porque a listagem pergunta
        // "este vídeo está marcado para esta pessoa?".
        builder.HasKey(f => new { f.VideoId, f.UserId });

        builder.HasOne<Video>().WithMany().HasForeignKey(f => f.VideoId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(f => f.UserId);
    }
}
