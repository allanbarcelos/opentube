using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenTube.Domain.Entities;

namespace OpenTube.Infrastructure.Persistence.Configurations;

public class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_entries");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).ValueGeneratedOnAdd();
        builder.Property(e => e.ActorEmail).HasMaxLength(254).IsRequired();
        builder.Property(e => e.Action).HasMaxLength(60).IsRequired();
        builder.Property(e => e.EntityType).HasMaxLength(40).IsRequired();
        builder.Property(e => e.Summary).HasMaxLength(500).IsRequired();
        builder.Property(e => e.IpHash).HasMaxLength(64);

        // O registro é lido em ordem cronológica, e filtrado por quem fez ou sobre o quê.
        builder.HasIndex(e => e.At);
        builder.HasIndex(e => new { e.EntityType, e.EntityId });
        builder.HasIndex(e => e.ActorEmail);
    }
}
