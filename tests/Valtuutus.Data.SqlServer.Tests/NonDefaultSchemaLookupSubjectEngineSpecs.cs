using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Data;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

[Collection("SqlServerAuthzSpec")]
public sealed class NonDefaultSchemaLookupSubjectEngineSpecs : BaseLookupSubjectEngineSpecs
{
    public NonDefaultSchemaLookupSubjectEngineSpecs(NonDefaultSchemaSqlServerFixture fixture) : base(fixture) { }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddSqlServer(
            _ => ((IWithDbConnectionFactory)Fixture).DbFactory,
            new ValtuutusSqlServerOptions(NonDefaultSchemaSqlServerFixture.Schema,
                "transactions", "relation_tuples", "attributes"));
    }
}
