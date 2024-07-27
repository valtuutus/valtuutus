using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Data.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

[Collection("SqlServerSpec")]
public sealed class LookupEntityEngineSpecs : DataLookupEntityEngineSpecs
{

    public LookupEntityEngineSpecs(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddSqlServer(_ => ((IWithDbConnectionFactory)_fixture).DbFactory);
    }
}