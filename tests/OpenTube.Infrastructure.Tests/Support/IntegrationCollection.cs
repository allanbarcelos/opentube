// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Support;

/// <summary>
/// Reúne os testes de integração numa coleção só, para que o banco e o storage efêmeros
/// sejam criados uma única vez por execução da suíte. A definição precisa morar no próprio
/// projeto de teste: o xUnit não enxerga coleções declaradas em outro assembly.
/// </summary>
[CollectionDefinition(Name)]
public class IntegrationCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<MinioFixture>
{
    public const string Name = "integracao";
}
