using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

[Collection("SqlServerSpec")]
public sealed class CheckEngineSpecs : BaseCheckEngineSpecs
{
    public CheckEngineSpecs(SqlServerFixture fixture) : base(fixture) {}

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddSqlServer(_ => ((IWithDbConnectionFactory)Fixture).DbFactory);
    }
}