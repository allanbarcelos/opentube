// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenTube.Domain.Entities;

namespace OpenTube.Infrastructure.Persistence.Configurations;

public class SupportThreadConfiguration : IEntityTypeConfiguration<SupportThread>
{
    public void Configure(EntityTypeBuilder<SupportThread> builder)
    {
        builder.ToTable("support_threads");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Status).HasConversion<int>();

        // A fila da administração é lida por estado e por recência.
        builder.HasIndex(t => new { t.Status, t.LastMessageAt });
        builder.HasIndex(t => new { t.VideoId, t.UserId });
        builder.HasIndex(t => t.UserId);

        builder.HasMany(t => t.Messages)
            .WithOne()
            .HasForeignKey(m => m.ThreadId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(t => t.Messages).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class SupportMessageConfiguration : IEntityTypeConfiguration<SupportMessage>
{
    public void Configure(EntityTypeBuilder<SupportMessage> builder)
    {
        builder.ToTable("support_messages");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Body).HasMaxLength(SupportMessage.MaxLength).IsRequired();

        builder.HasIndex(m => new { m.ThreadId, m.CreatedAt });
    }
}
