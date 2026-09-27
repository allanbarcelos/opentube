// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenTube.Domain.Entities;

namespace OpenTube.Infrastructure.Persistence.Configurations;

public class VideoRatingConfiguration : IEntityTypeConfiguration<VideoRating>
{
    public void Configure(EntityTypeBuilder<VideoRating> builder)
    {
        builder.ToTable("video_ratings", t =>
            t.HasCheckConstraint("ck_video_ratings_score", $"score BETWEEN {VideoRating.MinScore} AND {VideoRating.MaxScore}"));

        // Uma nota por pessoa e vídeo.
        builder.HasKey(r => new { r.VideoId, r.UserId });

        builder.HasOne<Video>().WithMany().HasForeignKey(r => r.VideoId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
