using OpenTube.TestSupport;

namespace OpenTube.Web.Tests.Support;

/// <summary>Coleção de integração da aplicação web, com banco e storage efêmeros.</summary>
[CollectionDefinition(Name)]
public class IntegrationCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<MinioFixture>
{
    public const string Name = "integracao";
}
