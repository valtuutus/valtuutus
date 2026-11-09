using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

public sealed class LookupSubjectEngineSpecs : BaseLookupSubjectEngineSpecs, IClassFixture<SqlServerFixture>
{

    public LookupSubjectEngineSpecs(SqlServerFixture fixture) : base(fixture){}

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddSqlServer(_ => ((IWithDbConnectionFactory)Fixture).DbFactory);
    }
}