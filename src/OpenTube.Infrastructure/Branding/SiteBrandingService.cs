// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Entities;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Branding;

/// <summary>Logotipo do site, pronto para ser servido.</summary>
/// <param name="Image">Bytes do PNG.</param>
/// <param name="Version">Versão do logotipo.</param>
public sealed record SiteLogo(byte[] Image, long Version);

/// <summary>
/// Personalização do site pela administração: nome, logotipo e o que o rodapé mostra. Cada
/// alteração descarta a identidade em cache, para valer já na página seguinte.
/// </summary>
public class SiteBrandingService(OpenTubeDbContext db, ISiteIdentity identity, TimeProvider clock)
{
    public async Task<SiteLogo?> GetLogoAsync(CancellationToken cancellationToken = default)
    {
        var personalizacao = await db.SiteBrandings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == SiteBranding.SingletonId, cancellationToken);

        return personalizacao?.Logo is { } logo ? new SiteLogo(logo, personalizacao.Version) : null;
    }

    /// <summary>
    /// Grava o nome e o rodapé e, quando vier, troca o logotipo. O logotipo é levado ao tamanho
    /// padrão antes de guardado; sem ele, o atual fica como está.
    /// </summary>
    public async Task<SiteBranding> SaveAsync(
        string name,
        bool showPoweredBy,
        bool showRepositoryLink,
        byte[]? logo,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        // A imagem é conferida antes de qualquer gravação: um logotipo inválido não deixa o
        // nome alterado pela metade.
        var reduzido = logo is null
            ? null
            : WatermarkImageProcessor.Normalize(
                logo,
                SiteBranding.LogoStandardWidth,
                SiteBranding.LogoStandardHeight,
                SiteBranding.LogoMinLongestSide,
                "The logo must be at least 32 pixels on its longest side.");

        var agora = clock.GetUtcNow();
        var personalizacao = await db.SiteBrandings.FirstOrDefaultAsync(s => s.Id == SiteBranding.SingletonId, cancellationToken);

        if (personalizacao is null)
        {
            personalizacao = SiteBranding.Define(name, showPoweredBy, showRepositoryLink, adminId, agora);
            db.SiteBrandings.Add(personalizacao);
        }
        else
        {
            personalizacao.Change(name, showPoweredBy, showRepositoryLink, adminId, agora);
        }

        if (reduzido is not null)
            personalizacao.SetLogo(reduzido, adminId, agora);

        await db.SaveChangesAsync(cancellationToken);
        identity.Invalidate();

        return personalizacao;
    }

    /// <summary>Tira o logotipo; a barra volta ao ícone de fábrica. <c>false</c> quando não havia.</summary>
    public async Task<bool> RemoveLogoAsync(Guid adminId, CancellationToken cancellationToken = default)
    {
        var personalizacao = await db.SiteBrandings.FirstOrDefaultAsync(s => s.Id == SiteBranding.SingletonId, cancellationToken);

        if (personalizacao?.Logo is null)
            return false;

        personalizacao.RemoveLogo(adminId, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        identity.Invalidate();

        return true;
    }
}
