// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.TestSupport;

namespace OpenTube.Worker.Tests.Support;

/// <summary>
/// Coleção de integração do worker. A definição precisa ser declarada em cada projeto de
/// teste: o xUnit não enxerga coleções de outro assembly.
/// </summary>
[CollectionDefinition(Name)]
public class IntegrationCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<MinioFixture>
{
    public const string Name = "integracao";
}
