using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Data;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

public sealed class NonDefaultSchemaLookupEntityEngineSpecs : BaseLookupEntityEngineSpecs, IClassFixture<NonDefaultSchemaSqlServerFixture>
{
    public NonDefaultSchemaLookupEntityEngineSpecs(NonDefaultSchemaSqlServerFixture fixture) : base(fixture) { }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddSqlServer(
            _ => ((IWithDbConnectionFactory)Fixture).DbFactory,
            ((SqlServerFixtureBase)Fixture).Options);
    }
}
