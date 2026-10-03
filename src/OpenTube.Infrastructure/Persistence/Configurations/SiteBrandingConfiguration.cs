// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenTube.Domain.Entities;

namespace OpenTube.Infrastructure.Persistence.Configurations;

public class SiteBrandingConfiguration : IEntityTypeConfiguration<SiteBranding>
{
    public void Configure(EntityTypeBuilder<SiteBranding> builder)
    {
        builder.ToTable("site_branding", tabela =>
            // Configuração única do site: a tabela nunca tem mais de uma linha.
            tabela.HasCheckConstraint("ck_site_branding_singleton", $"id = {SiteBranding.SingletonId}"));

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.Name).HasMaxLength(SiteBranding.MaxNameLength).IsRequired();
        builder.Ignore(s => s.Version);
    }
}
