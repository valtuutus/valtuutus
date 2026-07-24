using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

[Collection("SqlServerSpec")]
public sealed class CheckEngineV2ExplainSpecs : BaseCheckEngineV2RelationalExplainSpecs
{
    public CheckEngineV2ExplainSpecs(SqlServerFixture fixture) : base(fixture) { }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
        => services.AddSqlServer(_ => ((IWithDbConnectionFactory)Fixture).DbFactory);
}
