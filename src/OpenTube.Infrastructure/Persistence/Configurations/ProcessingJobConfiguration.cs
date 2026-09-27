// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenTube.Domain.Entities;

namespace OpenTube.Infrastructure.Persistence.Configurations;

public class ProcessingJobConfiguration : IEntityTypeConfiguration<ProcessingJob>
{
    public void Configure(EntityTypeBuilder<ProcessingJob> builder)
    {
        builder.ToTable("processing_jobs");
        builder.HasKey(j => j.Id);

        builder.Property(j => j.Kind).HasConversion<int>();
        builder.Property(j => j.Status).HasConversion<int>();
        builder.Property(j => j.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(j => j.LastError).HasMaxLength(4000);
        builder.Property(j => j.LockedBy).HasMaxLength(100);

        // Índice que sustenta a retirada da fila: pendentes, na ordem em que podem rodar.
        builder.HasIndex(j => new { j.Status, j.RunAfter });
        builder.HasIndex(j => new { j.Kind, j.TargetId });
    }
}
