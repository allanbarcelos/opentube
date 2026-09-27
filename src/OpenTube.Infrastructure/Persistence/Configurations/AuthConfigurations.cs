// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenTube.Domain.Entities;

namespace OpenTube.Infrastructure.Persistence.Configurations;

public class LoginCodeConfiguration : IEntityTypeConfiguration<LoginCode>
{
    public void Configure(EntityTypeBuilder<LoginCode> builder)
    {
        builder.ToTable("login_codes");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Email).HasMaxLength(254).IsRequired();
        builder.Property(c => c.EmailDomain).HasMaxLength(253).IsRequired();
        builder.Property(c => c.CodeHash).HasMaxLength(64).IsRequired();
        builder.Property(c => c.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(c => c.IpHash).HasMaxLength(64);
        builder.Property(c => c.Purpose).HasConversion<int>();

        // A conferência do link busca direto pelo resumo do token.
        builder.HasIndex(c => c.TokenHash).IsUnique();
        builder.HasIndex(c => new { c.Email, c.CreatedAt });
        builder.HasIndex(c => c.ExpiresAt);
    }
}

public class AuthSessionConfiguration : IEntityTypeConfiguration<AuthSession>
{
    public void Configure(EntityTypeBuilder<AuthSession> builder)
    {
        builder.ToTable("auth_sessions");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.IpHash).HasMaxLength(64);
        builder.Property(s => s.UserAgent).HasMaxLength(400);

        builder.HasIndex(s => new { s.UserId, s.RevokedAt });
        builder.HasIndex(s => s.ExpiresAt);
    }
}

public class AuthAttemptConfiguration : IEntityTypeConfiguration<AuthAttempt>
{
    public void Configure(EntityTypeBuilder<AuthAttempt> builder)
    {
        builder.ToTable("auth_attempts");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id).ValueGeneratedOnAdd();
        builder.Property(a => a.Scope).HasMaxLength(320).IsRequired();

        // Índice que sustenta a contagem por janela de tempo.
        builder.HasIndex(a => new { a.Scope, a.OccurredAt });
    }
}
