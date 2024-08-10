using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

[Collection("SqlServerSpec")]
public sealed class LookupSubjectEngineSpecs : BaseLookupSubjectEngineSpecs
{

    public LookupSubjectEngineSpecs(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddSqlServer(_ => ((IWithDbConnectionFactory)_fixture).DbFactory);
    }
}