using Valtuutus.Data.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

[Collection("PostgreSqlSpec")]
public sealed class LookupSubjectEngineSpecs : DataLookupSubjectEngineSpecs
{

    public LookupSubjectEngineSpecs(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    protected override void AddSpecificProvider(IValtuutusDataBuilder builder)
    {
        builder.AddPostgres(_ => _fixture.DbFactory);
    }
}