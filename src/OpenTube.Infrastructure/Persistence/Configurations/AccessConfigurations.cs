// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenTube.Domain.Entities;

namespace OpenTube.Infrastructure.Persistence.Configurations;

public class AccessGrantConfiguration : IEntityTypeConfiguration<AccessGrant>
{
    public void Configure(EntityTypeBuilder<AccessGrant> builder)
    {
        builder.ToTable("access_grants");
        builder.HasKey(g => g.Id);

        builder.Property(g => g.SubjectType).HasConversion<int>();
        builder.Property(g => g.TargetType).HasConversion<int>();
        builder.Property(g => g.SubjectValue).HasMaxLength(320).IsRequired();
        builder.Property(g => g.Note).HasMaxLength(500);

        // Índice que sustenta a busca de concessões aplicáveis a quem está pedindo.
        builder.HasIndex(g => new { g.SubjectType, g.SubjectValue });
        builder.HasIndex(g => new { g.TargetType, g.TargetId });
        builder.HasIndex(g => g.RevokedAt);

        // Concessões de um convite; as de antes dos convites ficam sem.
        builder.HasOne<Invitation>().WithMany().HasForeignKey(g => g.InvitationId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(g => g.InvitationId);
    }
}

public class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> builder)
    {
        builder.ToTable("invitations");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.Kind).HasConversion<int>();
        builder.Property(i => i.TargetType).HasConversion<int>();
        builder.Property(i => i.Note).HasMaxLength(500);

        // A lista de convites de um vídeo ou coleção.
        builder.HasIndex(i => new { i.TargetType, i.TargetId });
    }
}

public class CollectionConfiguration : IEntityTypeConfiguration<Collection>
{
    public void Configure(EntityTypeBuilder<Collection> builder)
    {
        builder.ToTable("collections");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name).HasMaxLength(200).IsRequired();
        builder.Property(c => c.Slug).HasMaxLength(80).IsRequired();
        builder.Property(c => c.Description).HasMaxLength(2000);

        builder.HasIndex(c => c.Slug).IsUnique();

        builder.HasMany(c => c.Videos)
            .WithOne()
            .HasForeignKey(v => v.CollectionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(c => c.Videos).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class CollectionVideoConfiguration : IEntityTypeConfiguration<CollectionVideo>
{
    public void Configure(EntityTypeBuilder<CollectionVideo> builder)
    {
        builder.ToTable("collection_videos");
        builder.HasKey(v => new { v.CollectionId, v.VideoId });

        // Consulta feita a cada avaliação de acesso: a quais coleções este vídeo pertence.
        builder.HasIndex(v => v.VideoId);
    }
}
