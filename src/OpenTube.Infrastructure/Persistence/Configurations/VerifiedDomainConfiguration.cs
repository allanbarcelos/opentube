// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenTube.Domain.Entities;

namespace OpenTube.Infrastructure.Persistence.Configurations;

public class VerifiedDomainConfiguration : IEntityTypeConfiguration<VerifiedDomain>
{
    public void Configure(EntityTypeBuilder<VerifiedDomain> builder)
    {
        builder.ToTable("verified_domains");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Name).HasMaxLength(253).IsRequired();
        builder.Property(d => d.VerificationToken).HasMaxLength(64).IsRequired();
        builder.Property(d => d.EntrySlug).HasMaxLength(120).IsRequired();
        builder.Property(d => d.ContactEmail).HasMaxLength(254);
        builder.Property(d => d.Note).HasMaxLength(500);

        builder.Property(d => d.AllowedEmails)
            .HasColumnType("text[]")
            .IsRequired();

        // Um domínio não pode ser cadastrado duas vezes, e a porta de entrada precisa ser única.
        builder.HasIndex(d => d.Name).IsUnique();
        builder.HasIndex(d => d.EntrySlug).IsUnique();
    }
}
