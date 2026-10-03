// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTube.Domain.Entities;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Branding;

/// <summary>Como o site se apresenta: o que a barra, os títulos, o rodapé e os emails usam.</summary>
/// <param name="Name">Nome do site.</param>
/// <param name="LogoVersion">Versão do logotipo, que vai no endereço dele; nula sem logotipo.</param>
/// <param name="LogoWidth">Largura do logotipo, em pixels.</param>
/// <param name="LogoHeight">Altura do logotipo, em pixels.</param>
/// <param name="ShowPoweredBy">O rodapé mostra o crédito "Desenvolvido por".</param>
/// <param name="ShowRepositoryLink">O rodapé mostra o link para o repositório do OpenTube.</param>
public sealed record SiteIdentity(
    string Name,
    long? LogoVersion,
    int? LogoWidth,
    int? LogoHeight,
    bool ShowPoweredBy,
    bool ShowRepositoryLink)
{
    /// <summary>A de fábrica, enquanto a administração não personalizar nada.</summary>
    public static readonly SiteIdentity Default = new(SiteBranding.DefaultName, null, null, null, true, true);

    public bool HasLogo => LogoVersion is not null;

    /// <summary>Título da aba: "Página — Nome", ou só o nome.</summary>
    public string Title(string? page = null) => string.IsNullOrWhiteSpace(page) ? Name : $"{page} — {Name}";
}

/// <summary>Leitura da identidade do site, para quem só precisa exibi-la.</summary>
public interface ISiteIdentity
{
    ValueTask<SiteIdentity> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Descarta o que está em cache: a próxima leitura vem do banco.</summary>
    void Invalidate();
}

/// <summary>
/// Identidade do site em cache. Toda página a lê no layout, e os emails a usam também; o banco
/// só é consultado quando ela muda ou a cada meio minuto, que é quanto demora para uma alteração
/// feita noutra réplica aparecer aqui.
/// </summary>
/// <remarks>
/// A consulta abre um escopo próprio: o layout a faz enquanto a página pode estar usando o
/// contexto do banco do pedido, e o EF recusa duas operações simultâneas no mesmo contexto.
/// </remarks>
public sealed class SiteIdentityProvider(IServiceScopeFactory scopeFactory, TimeProvider clock) : ISiteIdentity
{
    private static readonly TimeSpan Validade = TimeSpan.FromSeconds(30);

    private sealed record Guardada(SiteIdentity Identidade, DateTimeOffset LidaEm);

    private volatile Guardada? _guardada;

    public async ValueTask<SiteIdentity> GetAsync(CancellationToken cancellationToken = default)
    {
        var agora = clock.GetUtcNow();

        if (_guardada is { } guardada && agora - guardada.LidaEm < Validade)
            return guardada.Identidade;

        using var escopo = scopeFactory.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<OpenTubeDbContext>();

        // Sem os bytes do logotipo: a página só precisa saber se ele existe e qual a versão.
        var identidade = await db.SiteBrandings
            .AsNoTracking()
            .Where(s => s.Id == SiteBranding.SingletonId)
            .Select(s => new SiteIdentity(
                s.Name,
                s.Logo == null ? (long?)null : s.UpdatedAt.ToUnixTimeMilliseconds(),
                s.LogoWidth,
                s.LogoHeight,
                s.ShowPoweredBy,
                s.ShowRepositoryLink))
            .FirstOrDefaultAsync(cancellationToken) ?? SiteIdentity.Default;
        _guardada = new Guardada(identidade, agora);

        return identidade;
    }

    public void Invalidate() => _guardada = null;
}
