namespace OpenTube.Infrastructure.Tests.Support;

/// <summary>
/// Reúne os testes de integração numa coleção só, para que o banco e o storage efêmeros
/// sejam criados uma única vez por execução da suíte.
/// </summary>
[CollectionDefinition(Name)]
public class IntegrationCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<MinioFixture>
{
    public const string Name = "integracao";
}
