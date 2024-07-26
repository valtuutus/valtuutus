using Valtuutus.Data.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

[Collection("PostgreSqlSpec")]
public sealed class CheckEngineSpecs : DataCheckEngineSpecs
{
    public CheckEngineSpecs(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    protected override void AddSpecificProvider(IValtuutusDataBuilder builder)
    {
        builder.AddPostgres(_ => _fixture.DbFactory);
    }
}