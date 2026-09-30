// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenTube.Domain.Entities;

namespace OpenTube.Infrastructure.Persistence.Configurations;

public class CollectionFavoriteConfiguration : IEntityTypeConfiguration<CollectionFavorite>
{
    public void Configure(EntityTypeBuilder<CollectionFavorite> builder)
    {
        builder.ToTable("collection_favorites");

        // Uma marcação por pessoa e coleção. A coleção vem primeiro porque a home pergunta
        // "esta coleção está marcada para esta pessoa?".
        builder.HasKey(f => new { f.CollectionId, f.UserId });

        builder.HasOne<Collection>().WithMany().HasForeignKey(f => f.CollectionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(f => f.UserId);
    }
}
