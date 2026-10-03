// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Infrastructure.Branding;

namespace OpenTube.TestSupport;

/// <summary>
/// Identidade do site sem banco, para os testes que montam os serviços à mão. Sem argumento, é
/// a de fábrica.
/// </summary>
public sealed class IdentidadeFixa(SiteIdentity? identidade = null) : ISiteIdentity
{
    public SiteIdentity Identidade { get; set; } = identidade ?? SiteIdentity.Default;

    public ValueTask<SiteIdentity> GetAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Identidade);

    public void Invalidate() { }
}
