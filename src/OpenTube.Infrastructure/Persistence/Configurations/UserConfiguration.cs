using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenTube.Domain.Entities;

namespace OpenTube.Infrastructure.Persistence.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.Email).HasMaxLength(254).IsRequired();
        builder.Property(u => u.EmailDomain).HasMaxLength(253).IsRequired();
        builder.Property(u => u.DisplayName).HasMaxLength(120);

        // O email é a identidade da pessoa no sistema; duplicar significaria duas contas
        // para o mesmo ser humano, com históricos separados.
        builder.HasIndex(u => u.Email).IsUnique();
        builder.HasIndex(u => u.EmailDomain);
    }
}
