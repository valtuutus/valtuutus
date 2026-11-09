using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

public sealed class LookupEntityEngineSpecs : BaseLookupEntityEngineSpecs, IClassFixture<SqlServerFixture>
{

    public LookupEntityEngineSpecs(SqlServerFixture fixture) : base(fixture){}

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddSqlServer(_ => ((IWithDbConnectionFactory)Fixture).DbFactory);
    }
}