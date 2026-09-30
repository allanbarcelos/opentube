// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenTube.Domain.Entities;

namespace OpenTube.Infrastructure.Persistence.Configurations;

public class CollectionVideoSeenConfiguration : IEntityTypeConfiguration<CollectionVideoSeen>
{
    public void Configure(EntityTypeBuilder<CollectionVideoSeen> builder)
    {
        builder.ToTable("collection_video_seen");

        builder.HasKey(s => new { s.UserId, s.CollectionId, s.VideoId });

        builder.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Collection>().WithMany().HasForeignKey(s => s.CollectionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Video>().WithMany().HasForeignKey(s => s.VideoId).OnDelete(DeleteBehavior.Cascade);
    }
}
