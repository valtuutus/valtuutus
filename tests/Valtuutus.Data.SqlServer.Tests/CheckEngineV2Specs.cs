using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

[Collection("SqlServerSpec")]
public sealed class CheckEngineV2Specs : BaseCheckEngineSpecs
{
    public CheckEngineV2Specs(SqlServerFixture fixture) : base(fixture) {}
    protected override bool UseCheckV2 => true;

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddSqlServer(_ => ((IWithDbConnectionFactory)Fixture).DbFactory);
    }
}
