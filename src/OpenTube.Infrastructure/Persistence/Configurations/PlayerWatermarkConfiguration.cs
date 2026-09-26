using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenTube.Domain.Entities;

namespace OpenTube.Infrastructure.Persistence.Configurations;

public class PlayerWatermarkConfiguration : IEntityTypeConfiguration<PlayerWatermark>
{
    public void Configure(EntityTypeBuilder<PlayerWatermark> builder)
    {
        builder.ToTable("player_watermark", tabela =>
            // Configuração única do acervo: a tabela nunca tem mais de uma linha.
            tabela.HasCheckConstraint("ck_player_watermark_singleton", $"id = {PlayerWatermark.SingletonId}"));

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.Image).IsRequired();
        builder.Ignore(m => m.Version);
    }
}
