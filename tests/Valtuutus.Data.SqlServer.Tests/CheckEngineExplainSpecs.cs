using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

[Collection("SqlServerSpec")]
public sealed class CheckEngineExplainSpecs : BaseCheckEngineExplainSpecs
{
    public CheckEngineExplainSpecs(SqlServerFixture fixture) : base(fixture) { }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
        => services.AddSqlServer(_ => ((IWithDbConnectionFactory)Fixture).DbFactory);
}
